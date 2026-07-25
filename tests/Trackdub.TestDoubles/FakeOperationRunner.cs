using System.ComponentModel;
using System.Reactive.Linq;
using Trackdub.App.Avalonia.Services;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeOperationRunner : IOperationRunner
{
    private bool _isPipelineBusy;
    private bool _isLoadBusy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsPipelineBusy => _isPipelineBusy;

    public bool IsLoadBusy => _isLoadBusy;

    public bool IsBusy => IsPipelineBusy || IsLoadBusy;

    public IObservable<PipelineProgressEvent> ProgressEvents =>
        Observable.Never<PipelineProgressEvent>();

    public Task RunAsync(
        string operationName,
        Func<CancellationToken, IProgress<PipelineProgressEvent>, Task> operation,
        OperationRunnerLane lane = OperationRunnerLane.Pipeline) =>
        RunInternalAsync(operation, lane, failWhenBusy: false);

    public Task<bool> TryRunAsync(
        string operationName,
        Func<CancellationToken, IProgress<PipelineProgressEvent>, Task> operation,
        OperationRunnerLane lane = OperationRunnerLane.Pipeline) =>
        RunInternalAsync(operation, lane, failWhenBusy: true);

    private async Task<bool> RunInternalAsync(
        Func<CancellationToken, IProgress<PipelineProgressEvent>, Task> operation,
        OperationRunnerLane lane,
        bool failWhenBusy)
    {
        if (IsLaneBusy(lane))
        {
            return !failWhenBusy;
        }

        SetLaneBusy(lane, true);
        try
        {
            await operation(CancellationToken.None, new Progress<PipelineProgressEvent>());
            return true;
        }
        finally
        {
            SetLaneBusy(lane, false);
        }
    }

    public void Cancel() { }

    private bool IsLaneBusy(OperationRunnerLane lane) =>
        lane == OperationRunnerLane.Load ? IsLoadBusy : IsPipelineBusy;

    private void SetLaneBusy(OperationRunnerLane lane, bool busy)
    {
        if (lane == OperationRunnerLane.Load)
        {
            if (_isLoadBusy == busy)
            {
                return;
            }

            _isLoadBusy = busy;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadBusy)));
        }
        else
        {
            if (_isPipelineBusy == busy)
            {
                return;
            }

            _isPipelineBusy = busy;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPipelineBusy)));
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
    }
}
