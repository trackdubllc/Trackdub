using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.CosyVoice;

internal sealed class CosyVoiceOnnxSessions : IDisposable
{
    private CosyVoiceOnnxSessions(
        OnnxExecutionSessionFactory.SingleSessionLease campplus,
        OnnxExecutionSessionFactory.SingleSessionLease speechTokenizer,
        OnnxExecutionSessionFactory.SingleSessionLease textEncoder,
        OnnxExecutionSessionFactory.SingleSessionLease tokenGenerator,
        OnnxExecutionSessionFactory.SingleSessionLease flowEncoder,
        OnnxExecutionSessionFactory.SingleSessionLease flowEstimator,
        OnnxExecutionSessionFactory.SingleSessionLease f0Predictor,
        OnnxExecutionSessionFactory.SingleSessionLease source,
        OnnxExecutionSessionFactory.SingleSessionLease vocoder,
        string selectedProvider)
    {
        Campplus = campplus;
        SpeechTokenizer = speechTokenizer;
        TextEncoder = textEncoder;
        TokenGenerator = tokenGenerator;
        FlowEncoder = flowEncoder;
        FlowEstimator = flowEstimator;
        F0Predictor = f0Predictor;
        Source = source;
        Vocoder = vocoder;
        SelectedProvider = selectedProvider;
    }

    public OnnxExecutionSessionFactory.SingleSessionLease Campplus { get; }

    public OnnxExecutionSessionFactory.SingleSessionLease SpeechTokenizer { get; }

    public OnnxExecutionSessionFactory.SingleSessionLease TextEncoder { get; }

    public OnnxExecutionSessionFactory.SingleSessionLease TokenGenerator { get; }

    public OnnxExecutionSessionFactory.SingleSessionLease FlowEncoder { get; }

    public OnnxExecutionSessionFactory.SingleSessionLease FlowEstimator { get; }

    public OnnxExecutionSessionFactory.SingleSessionLease F0Predictor { get; }

    public OnnxExecutionSessionFactory.SingleSessionLease Source { get; }

    public OnnxExecutionSessionFactory.SingleSessionLease Vocoder { get; }

    public string SelectedProvider { get; }

    public static async Task<CosyVoiceOnnxSessions> CreateAsync(
        CosyVoiceModelFiles modelFiles,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        async Task<OnnxExecutionSessionFactory.SingleSessionLease> Load(string path) =>
            await OnnxExecutionSessionFactory.CreateSingleAsync(path, provider, cancellationToken).ConfigureAwait(false);

        var campplus = await Load(modelFiles.CampPlusPath).ConfigureAwait(false);
        var speechTokenizer = await Load(modelFiles.SpeechTokenizerPath).ConfigureAwait(false);
        var textEncoder = await Load(modelFiles.TextEncoderPath).ConfigureAwait(false);
        var tokenGenerator = await Load(modelFiles.TokenGeneratorPath).ConfigureAwait(false);
        var flowEncoder = await Load(modelFiles.FlowEncoderPath).ConfigureAwait(false);
        var flowEstimator = await Load(modelFiles.FlowDecoderEstimatorPath).ConfigureAwait(false);
        var f0Predictor = await Load(modelFiles.HiftF0PredictorPath).ConfigureAwait(false);
        var source = await Load(modelFiles.HiftSourcePath).ConfigureAwait(false);
        var vocoder = await Load(modelFiles.HiftVocoderPath).ConfigureAwait(false);

        return new CosyVoiceOnnxSessions(
            campplus,
            speechTokenizer,
            textEncoder,
            tokenGenerator,
            flowEncoder,
            flowEstimator,
            f0Predictor,
            source,
            vocoder,
            textEncoder.SelectedProvider);
    }

    public void Dispose()
    {
        Campplus.Dispose();
        SpeechTokenizer.Dispose();
        TextEncoder.Dispose();
        TokenGenerator.Dispose();
        FlowEncoder.Dispose();
        FlowEstimator.Dispose();
        F0Predictor.Dispose();
        Source.Dispose();
        Vocoder.Dispose();
    }
}
