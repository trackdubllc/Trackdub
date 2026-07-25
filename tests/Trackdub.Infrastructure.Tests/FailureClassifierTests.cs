using Trackdub.Contracts.Diagnostics;
using Trackdub.Infrastructure.Diagnostics;

namespace Trackdub.Infrastructure.Tests;

public sealed class FailureClassifierTests
{
    [Fact]
    public void Classifies_BadImageFormatException_as_ModelLoadFailure()
    {
        var ex = new BadImageFormatException("Invalid ONNX model file.");
        Assert.Equal(FailureCategory.ModelLoadFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_FileNotFoundException_with_model_path_as_ModelLoadFailure()
    {
        var ex = new FileNotFoundException("File not found.", "models/whisper-tiny.onnx");
        Assert.Equal(FailureCategory.ModelLoadFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_FileNotFoundException_with_onnx_path_as_ModelLoadFailure()
    {
        var ex = new FileNotFoundException("model-cache/whisper-tiny/model.onnx not found.");
        Assert.Equal(FailureCategory.ModelLoadFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_InvalidOperationException_with_onnx_keyword_as_ModelLoadFailure()
    {
        var ex = new InvalidOperationException("Failed to create ONNX inference session.");
        Assert.Equal(FailureCategory.ModelLoadFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_Inference_keyword_without_model_load_signal_as_InferenceFailure()
    {
        var ex = new InvalidOperationException("Inference runtime returned an unexpected status.");
        Assert.Equal(FailureCategory.InferenceFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_ui_dispatcher_failure_as_UiCrash()
    {
        var ex = new InvalidOperationException("UI dispatcher failed while rendering the window.");
        Assert.Equal(FailureCategory.UiCrash, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Does_not_classify_build_substring_as_UiCrash()
    {
        var ex = new InvalidOperationException("Build project failed before export.");
        Assert.Equal(FailureCategory.UnknownError, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_InvalidOperationException_with_tensor_keyword_as_InferenceFailure()
    {
        var ex = new InvalidOperationException("Unexpected tensor shape [1, 80, 3000].");
        Assert.Equal(FailureCategory.InferenceFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_InvalidOperationException_with_transcription_keyword_as_InferenceFailure()
    {
        var ex = new InvalidOperationException("Transcription failed.");
        Assert.Equal(FailureCategory.InferenceFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_InvalidOperationException_with_translation_keyword_as_InferenceFailure()
    {
        var ex = new InvalidOperationException("Translation failed.");
        Assert.Equal(FailureCategory.InferenceFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_InvalidOperationException_with_tts_keyword_as_InferenceFailure()
    {
        var ex = new InvalidOperationException("TTS engine failed to synthesize audio.");
        Assert.Equal(FailureCategory.InferenceFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_InvalidOperationException_with_audio_keyword_as_MediaDecodeFailure()
    {
        var ex = new InvalidOperationException("Failed to decode audio stream.");
        Assert.Equal(FailureCategory.MediaDecodeFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_NotSupportedException_with_codec_keyword_as_MediaDecodeFailure()
    {
        var ex = new NotSupportedException("Unsupported video codec format.");
        Assert.Equal(FailureCategory.MediaDecodeFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_DbException_as_PersistenceFailure()
    {
        var ex = new Microsoft.Data.Sqlite.SqliteException("SQLITE_ERROR: no such table", 1);
        Assert.Equal(FailureCategory.PersistenceFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_IOException_with_sqlite_keyword_as_PersistenceFailure()
    {
        var ex = new IOException("Cannot open SQLite database file.");
        Assert.Equal(FailureCategory.PersistenceFailure, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_generic_exception_as_UnknownError()
    {
        var ex = new Exception("Unexpected error occurred.");
        Assert.Equal(FailureCategory.UnknownError, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classifies_ArgumentException_as_UnknownError()
    {
        var ex = new ArgumentException("Invalid argument value.");
        Assert.Equal(FailureCategory.UnknownError, FailureClassifier.Classify(ex));
    }

    [Fact]
    public void Throws_on_null_exception()
    {
        Assert.Throws<ArgumentNullException>(() => FailureClassifier.Classify(null!));
    }
}
