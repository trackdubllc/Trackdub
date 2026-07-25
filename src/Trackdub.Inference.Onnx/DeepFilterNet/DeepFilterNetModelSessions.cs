using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.DeepFilterNet;

internal sealed class DeepFilterNetModelSessions(
    OnnxExecutionSessionFactory.SingleSessionLease enc,
    OnnxExecutionSessionFactory.SingleSessionLease erbDec,
    OnnxExecutionSessionFactory.SingleSessionLease dfDec) : IDisposable
{
    public OnnxExecutionSessionFactory.SingleSessionLease Enc { get; } = enc;
    public OnnxExecutionSessionFactory.SingleSessionLease ErbDec { get; } = erbDec;
    public OnnxExecutionSessionFactory.SingleSessionLease DfDec { get; } = dfDec;

    public static async Task<DeepFilterNetModelSessions> CreateAsync(
        DeepFilterNetModelPaths paths,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        OnnxExecutionSessionFactory.SingleSessionLease? enc = null;
        OnnxExecutionSessionFactory.SingleSessionLease? erbDec = null;
        OnnxExecutionSessionFactory.SingleSessionLease? dfDec = null;
        try
        {
            enc = await OnnxExecutionSessionFactory
                .CreatePooledSingleAsync("deepfilternet3-enc", paths.EncPath, provider, cancellationToken)
                .ConfigureAwait(false);
            erbDec = await OnnxExecutionSessionFactory
                .CreatePooledSingleAsync("deepfilternet3-erb-dec", paths.ErbDecPath, provider, cancellationToken)
                .ConfigureAwait(false);
            dfDec = await OnnxExecutionSessionFactory
                .CreatePooledSingleAsync("deepfilternet3-df-dec", paths.DfDecPath, provider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            enc?.Dispose();
            erbDec?.Dispose();
            dfDec?.Dispose();
            throw;
        }

        return new DeepFilterNetModelSessions(enc, erbDec, dfDec);
    }

    public void Dispose()
    {
        Enc.Dispose();
        ErbDec.Dispose();
        DfDec.Dispose();
    }
}
