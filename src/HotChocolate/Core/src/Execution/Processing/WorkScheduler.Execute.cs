using System;
using System.Threading.Tasks;
using static System.Environment;

namespace HotChocolate.Execution.Processing;

internal sealed partial class WorkScheduler
{
    private readonly IExecutionTask?[] _buffer = new IExecutionTask?[ProcessorCount * 2];

    public async Task ExecuteAsync()
    {
        AssertNotPooled();

        try
        {
            await ExecuteInternalAsync(_buffer);
        }
        finally
        {
            _buffer.AsSpan().Clear();
        }
    }

    private async Task ExecuteInternalAsync(IExecutionTask?[] buffer)
    {
RESTART:
        _diagnosticEvents.StartProcessing(_requestContext);

        try
        {
            do
            {
                var work = TryTake(buffer);

                if (work is not 0)
                {
                    if (!buffer[0]!.IsSerial)
                    {
                        // if work is not serial we will just enqueue it and not wait
                        // for it to finish.
                        for (var i = 0; i < work; i++)
                        {
                            buffer[i]!.BeginExecute(_ct);
                            buffer[i] = null;
                        }
                    }
                    else
                    {
                        // if work is serial we will synchronize the batch dispatcher and
                        // wait for the task to be finished.
                        try
                        {
                            _batchDispatcher.DispatchOnSchedule = true;
                            var task = buffer[0]!;
                            task.BeginExecute(_ct);
                            await task.WaitForCompletionAsync(_ct).ConfigureAwait(false);
                            buffer[0] = null;
                        }
                        finally
                        {
                            _batchDispatcher.DispatchOnSchedule = false;
                        }
                    }
                }
                else
                {
                    break;
                }

            } while (!_ct.IsCancellationRequested);
        }
        catch (Exception ex)
        {
            if (!_ct.IsCancellationRequested)
            {
                HandleError(ex);
            }
        }

        TryDispatchOrComplete();

        if (await TryPauseAsync().ConfigureAwait(false))
        {
            goto RESTART;
        }

        _ct.ThrowIfCancellationRequested();
    }

    private int TryTake(IExecutionTask?[] buffer)
    {
        var size = 0;

        lock (_sync)
        {
            var isDefault = !_work.IsEmpty || _work.HasRunningTasks;
            var work = isDefault ? _work : _serial;

            if (isDefault)
            {
                // The default behavior for tasks is that they can be executed in parallel.
                // We will always try to dequeue multiple tasks at once so that we avoid having
                // many lock interactions.
                for (var i = 0; i < buffer.Length; i++)
                {
                    if (!work.TryTake(out var task))
                    {
                        break;
                    }

                    size++;
                    buffer[i] = task;
                }
            }
            else
            {
                // For serial work we dequeue one task at a time.
                // Parallel work is always preferred, so we take a single serial task and see if
                // this results in more parallel work.
                if (work.TryTake(out var task))
                {
                    size = 1;
                    buffer[0] = task;
                }
            }
        }

        return size;
    }

    private void BatchDispatcherEventHandler(object? source, EventArgs args)
    {
        lock (_sync)
        {
            _hasBatches = true;
        }

        _pause.TryContinue();
    }

    private void HandleError(Exception exception)
    {
        var error =
            _errorHandler
                .CreateUnexpectedError(exception)
                .SetCode(ErrorCodes.Execution.TaskProcessingError)
                .Build();

        error = _errorHandler.Handle(error);

        if (error is AggregateError aggregateError)
        {
            foreach (var innerError in aggregateError.Errors)
            {
                _result.AddError(innerError);
            }
        }
        else
        {
            _result.AddError(error);
        }
    }

    private void TryDispatchOrComplete()
    {
        if (!_isCompleted)
        {
            lock (_sync)
            {
                if (!_isCompleted)
                {
                    var isWaitingForTaskCompletion = _work.HasRunningTasks && _work.IsEmpty;
                    var hasWork = !_work.IsEmpty || !_serial.IsEmpty;

                    if (isWaitingForTaskCompletion && _hasBatches)
                    {
                        _hasBatches = false;
                        _pause.Reset();
                        _batchDispatcher.BeginDispatch(_ct);
                    }
                    else if (!isWaitingForTaskCompletion && !_hasBatches && !hasWork)
                    {
                        _isCompleted = true;
                    }
                }
            }
        }
    }

    private async ValueTask<bool> TryPauseAsync()
    {
        if (!_isCompleted)
        {
            if (_pause.IsPaused)
            {
                await _pause;
            }
            return true;
        }
        return false;
    }
}
