Source: https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b

Nemotron 3.5 ASR
This model is the multilingual extension of [nvidia/nemotron-speech-streaming-en-0.6b](<https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b>) , adding language-ID prompt conditioning to support transcription across 40 language-locales from a single model.
Nemotron 3.5 ASR is a multilingual, streaming Automatic Speech Recognition (ASR) model engineered to deliver high-quality multilingual transcription across both low-latency streaming and high-throughput batch workloads. Developed by NVIDIA, this 600M parameter model transcribes speech into text with native support for punctuation and capitalization, and offers runtime flexibility with configurable chunk sizes, including 80ms, 160ms, 320ms, 560ms, and 1120ms.
By leveraging a state-of-the-art Cache-Aware FastConformer-RNNT architecture, the model eliminates redundant overlapping computations common in traditional "buffered" streaming. This allows it to process only new audio chunks while reusing cached encoder context, significantly improving computational efficiency and minimizing end-to-end delay without sacrificing accuracy.
It was trained on a massive ASR dataset and is engineered to perform across diverse and challenging acoustic conditions.
This model is ready for commercial use.
Release Date
Hugging Face [06/04/2026] via [https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b>)
Why Choose Nemotron 3.5 ASR?
🌍 Single Multilingual Model: Transcribes 40 language-locales from one model through language-ID prompt conditioning, with optional automatic language detection.
⚡ Native Streaming Architecture: Cache-aware design enables efficient processing of continuous audio streams, designed and optimized for low-latency voice agent applications.
💰 Improved Operational Efficiency: Delivers superior throughput compared to traditional buffered streaming approaches. This allows for a higher number of parallel streams within the same GPU memory constraints, directly reducing operational costs for production environments.
🎛️ Dynamic Runtime Flexibility: Choose the optimal operating point on the latency-accuracy Pareto curve at inference time. No re-training is required to adjust for different use-case requirements.
📝 Punctuation & Capitalization: Built-in support for punctuation and capitalization in output text.
🔧 Fine-tuning: Check our [blog post](<https://huggingface.co/blog/nvidia/fine-tuning-nemotron-35-asr>) of how to fine-tune Nemotron 3.5 ASR to improve these languages , including before/after results.
Supported Languages
The model supports 40 language-locales in total, across three tiers:
Transcription-ready (19 locales): highest-accuracy ASR, ready out of the box.
Broad-coverage (13 locales): production ASR across an additional 13 locales.
Adaptation-ready (8 locales): recognized by the tokenizer; fine-tune on in-domain data to unlock full transcription.
Tier Languages (locales)
Transcription-ready (19 locales) English (en-US, en-GB), Spanish (es-US, es-ES), French (fr-FR, fr-CA), Italian (it-IT), Portuguese (pt-BR, pt-PT), Dutch (nl-NL), German (de-DE), Turkish (tr-TR), Russian (ru-RU), Arabic (ar-AR), Hindi (hi-IN), Japanese (ja-JP), Korean (ko-KR), Vietnamese (vi-VN), Ukrainian (uk-UA)
Broad-coverage (13 locales) Polish (pl-PL), Swedish (sv-SE), Czech (cs-CZ), Norwegian Bokmål (nb-NO), Danish (da-DK), Bulgarian (bg-BG), Finnish (fi-FI), Croatian (hr-HR), Slovak (sk-SK), Mandarin (zh-CN), Hungarian (hu-HU), Romanian (ro-RO), Estonian (et-EE)
Adaptation-ready (8 locales) Greek (el-GR), Lithuanian (lt-LT), Latvian (lv-LV), Maltese (mt-MT), Slovenian (sl-SI), Hebrew (he-IL), Thai (th-TH), Norwegian Nynorsk (nn-NO)
Note: Transcription-ready and broad-coverage locales ( 32 total ) produce ASR transcription out of the box; adaptation-ready locales require fine-tuning on in-domain data to enable full transcription. The model supports uppercase and lowercase letters, punctuation, spaces, and apostrophes.
Note: We would recommend [Nemotron ASR Streaming (English)](<https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b>) model for English-only transcription use cases. For all other transcription ready locales, we recommend Nemotron 3.5 ASR to leverage its expanded multilingual capabilities.
Automatic language detection / language tagging: When run with target_lang=auto , the model detects the spoken language and emits the corresponding language code/tag in the output following the terminal punctuation. This lets a single deployment transcribe mixed-language traffic and automatically label each utterance with its detected language — no separate language-ID component required.
Model Architecture
Architecture Type: FastConformer-CacheAware-RNNT with Prompt
This model consists of a cache-aware streaming Parakeet (FastConformer) encoder with an RNN-T decoder and language-ID prompt conditioning. It is based on the Cache-Aware [1] FastConformer [2] architecture with 24 encoder layers and an RNNT (Recurrent Neural Network Transducer) decoder. The cache-aware streaming design enables efficient processing of audio in chunks while maintaining context from previous frames. Unlike buffered inference, this model maintains caches for all encoder self-attention and convolution layers. This enables reuse of hidden states at every streaming step, where cached activations eliminate redundant computations. As a result, there are no overlapping computations; each processed frame is strictly non-overlapping. This model leverages prompts to guide the transcription process, enabling language-specific transcription from a single ASR model through language ID conditioning.
The language-ID prompt is fused with the acoustic representation as follows:
FastConformer encoder processes audio into an acoustic embedding of shape (D=1024, T).
Language Encoding expands a 128-dim one-hot language vector across the time axis → (K=128, T), broadcasting the language identity to every frame.
Concatenation along the feature axis → fused tensor (D + K, T).
Projection layer maps the fused features to the RNNT decoder.
Network Architecture:
Encoder: Cache-Aware FastConformer with 24 layers
Decoder: RNNT (Recurrent Neural Network Transducer)
Parameters: 600M
This model was developed based on [nvidia/nemotron-speech-streaming-en-0.6b](<https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b>) .
Results at a Glance
ASR performance is measured using Word Error Rate (WER) on the FLEURS test sets. Accuracy stays strong across both modes and improves as the chunk size grows, while remaining competitive even at the lowest-latency 80ms setting. Full tables are in Performance .
Note: Japanese and Korean are measured using Character Error Rate (CER) rather than WER, as is standard for these languages.
Throughput & Efficiency
Despite being roughly half the size (0.6B vs. 1.1B), Nemotron 3.5 ASR serves far more concurrent streams at far lower latency than the [Parakeet RNNT 1.1B multilingual model](<https://build.nvidia.com/nvidia/parakeet-1_1b-rnnt-multilingual-asr>) , which runs on buffered streaming. The cache-aware streaming design avoids the redundant recomputation of buffered inference, so a single H100 can sustain dramatically higher concurrency at every chunk size — directly lowering the cost per stream in production. At the lowest-latency 80ms setting, Nemotron sustains ~17× more concurrent streams (240 vs. 14); at the 1120ms setting it sustains 6× more (2,400 vs. 400). The latency-vs-concurrency curves tell the same story: Nemotron (solid green) holds low final-token latency well past 1,000 parallel requests, while Parakeet RNNT 1.1B (dashed blue) saturates after only a few hundred.
Measured on a single NVIDIA H100. Throughput is the number of real-time streams sustainable in parallel; latency is the median final-token latency at a given level of concurrency.
Explore more from NVIDIA
For documentation, deployment guides, enterprise-ready APIs, and the latest open models—including Nemotron and other cutting-edge speech, translation, and generative AI—visit the NVIDIA Developer Portal at [developer.nvidia.com](<https://developer.nvidia.com/>) .
Join the community to access tools, support, and resources to accelerate your development with NVIDIA's NeMo, Speech NIM, and foundation models.
What is [Nemotron](<https://www.nvidia.com/en-us/ai-data-science/foundation-models/nemotron/>) ?
NVIDIA Developer [Nemotron](<https://developer.nvidia.com/nemotron>)
[NVIDIA Speech NIM](<https://docs.nvidia.com/nim/speech/latest/about/index.html>)
[NeMo Documentation](<https://docs.nvidia.com/nemo-framework/user-guide/latest/nemotoolkit/asr/models.html>)
Also, check out the following NVIDIA speech models:
Nemotron ASR Streaming (English) (Nemotron 3 ASR) - [https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b](<https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b>)
Multitalker Parakeet Streaming - [https://huggingface.co/nvidia/multitalker-parakeet-streaming-0.6b-v1](<https://huggingface.co/nvidia/multitalker-parakeet-streaming-0.6b-v1>)
Parakeet Realtime EOU - [https://huggingface.co/nvidia/parakeet_realtime_eou_120m-v1](<https://huggingface.co/nvidia/parakeet_realtime_eou_120m-v1>)
How to Use this Model
There are several ways to use this model. Choose the one that fits your needs.
Run locally with NeMo-Speech.cpp
[NeMo-Speech.cpp](<https://github.com/NVIDIA/NeMo-Speech.cpp>) provides a
lightweight native C++ runtime for local inference with
this model. After [installing the runtime](<https://github.com/NVIDIA/NeMo-Speech.cpp#installation>) :
hf download nvidia/nemotron-3.5-asr-streaming-0.6b \
nemotron-3.5-asr-streaming-0.6b.q8_0.gguf \
--local-dir models
nemo-speech transcribe audio.wav \
--model models/nemotron-3.5-asr-streaming-0.6b.q8_0.gguf \
--language en-US
Use another supported locale or --language auto for automatic language
detection. See the [NeMo-Speech.cpp documentation](<https://github.com/NVIDIA/NeMo-Speech.cpp>) for more details.
NVIDIA NeMo
To train, fine-tune or perform inference with this model, install [NVIDIA NeMo](<https://github.com/NVIDIA/NeMo>) [4] after installing Python 3.11 or later, Cython, and a recent PyTorch version.
apt-get update && apt-get install -y libsndfile1 ffmpeg
pip install Cython packaging
pip install git+https://github.com/NVIDIA/NeMo.git@main #egg=nemo_toolkit[asr]
Loading the Model
import nemo.collections.asr as nemo_asr
asr_model = nemo_asr.models.ASRModel.from_pretrained(model_name= "nvidia/nemotron-3.5-asr-streaming-0.6b" )
Streaming Inference
You can use the cache-aware streaming inference script from NeMo - [NeMo/examples/asr/asr_cache_aware_streaming/speech_to_text_cache_aware_streaming_infer.py](<https://github.com/NVIDIA-NeMo/NeMo/blob/main/examples/asr/asr_cache_aware_streaming/speech_to_text_cache_aware_streaming_infer.py>)
This is a prompt-conditioned multilingual model: pass the target language with target_lang (e.g. en-US , es-ES , de-DE ), or use target_lang=auto for automatic language detection.
cd NeMo
python examples/asr/asr_cache_aware_streaming/speech_to_text_cache_aware_streaming_infer.py \
model_path=<model_path> \
dataset_manifest=<dataset_manifest> \
batch_size=<batch_size> \
target_lang=<lang_id> \ #language key (e.g. en-US) or "auto" for automatic language detection att_context_size= "[56,13]" \ #set the second value to the desired right context from {0,1,3,6,13} strip_lang_tags= true \ #true: remove the detected language tag from the text; false: keep it in the output output_path=<output_folder>
strip_lang_tags controls how the detected language tag is handled in the output. The model appends a language tag (e.g. <en-US> ) after the transcript's terminal punctuation:
strip_lang_tags=false (keep): the tag is left in the output, so you can read the detected language directly from each utterance — useful for mixed-language traffic and language labeling.
strip_lang_tags=true (remove): the tag is stripped, leaving only the clean transcript text — useful when you only need the spoken words.
Setting up Streaming Configuration
Latency is defined by the att_context_size param, where att_context_size = {num_frames_left_context, num_frame_right_context} , all measured in 80ms frames :
[56, 0]: Chunk size = 1 (1 × 80ms = 0.08s)
[56, 1]: Chunk size = 2 (2 × 80ms = 0.16s)
[56, 3]: Chunk size = 4 (4 × 80ms = 0.32s)
[56, 6]: Chunk size = 7 (7 × 80ms = 0.56s)
[56, 13]: Chunk size = 14 (14 × 80ms = 1.12s)
Here, chunk size = current frame + right context; each chunk is processed in non-overlapping fashion.
🤗 Transformers usage
This checkpoint also runs with [🤗 Transformers](<https://github.com/huggingface/transformers>) . The target language is passed through the processor's language argument: a locale such as en-US / de-DE , a bare code such as de , or auto for automatic language detection. In auto mode the model appends an <xx-XX> language tag after the transcript's terminal punctuation; it is a special token, so decoding with skip_special_tokens=True strips it (clean transcript) and skip_special_tokens=False keeps it for language labeling.
Nemotron3_5Asr is available in 🤗 Transformers starting from v5.13.0.
pip install "transformers>=5.13.0"
➡️ Pipeline from transformers import pipeline
pipe = pipeline( "automatic-speech-recognition" , model= "nvidia/nemotron-3.5-asr-streaming-0.6b" )
out = pipe( "https://huggingface.co/datasets/hf-internal-testing/dummy-audio-samples/resolve/main/bcn_weather.mp3" ) print (out)
The pipeline uses the default language prompt (index 0, en-US ). For explicit language conditioning or automatic detection, pass the processor's language argument (see the AutoModel example below).
➡️ Offline transcription from transformers import AutoModelForRNNT, AutoProcessor from transformers.audio_utils import load_audio
model_id = "nvidia/nemotron-3.5-asr-streaming-0.6b" processor = AutoProcessor.from_pretrained(model_id)
model = AutoModelForRNNT.from_pretrained(model_id, device_map= "auto" )
audio = load_audio( "https://huggingface.co/datasets/hf-internal-testing/dummy-audio-samples/resolve/main/bcn_weather.mp3" ,
sampling_rate=processor.feature_extractor.sampling_rate,
) # Condition on a known language ... inputs = processor(audio, sampling_rate=processor.feature_extractor.sampling_rate, language= "en-US" )
inputs.to(model.device, dtype=model.dtype)
output = model.generate(**inputs, return_dict_in_generate= True ) print (processor.decode(output.sequences, skip_special_tokens= True )) # ... or let the model detect it and keep the emitted <xx-XX> language tag. inputs = processor(audio, sampling_rate=processor.feature_extractor.sampling_rate, language= "auto" )
inputs.to(model.device, dtype=model.dtype)
output = model.generate(**inputs, return_dict_in_generate= True ) print (processor.decode(output.sequences, skip_special_tokens= False ))
➡️ Streaming transcription from threading import Thread from transformers import AutoModelForRNNT, AutoProcessor, TextIteratorStreamer from transformers.audio_utils import load_audio
model_id = "nvidia/nemotron-3.5-asr-streaming-0.6b" processor = AutoProcessor.from_pretrained(model_id)
model = AutoModelForRNNT.from_pretrained(model_id, device_map= "auto" )
processor.set_num_lookahead_tokens( 6 ) print ( f"Streaming latency: {processor.streaming_latency_ms} ms" ) # The language prompt rides along on every chunk; use a locale (e.g. "de-DE") or "auto". language = "en-US" sampling_rate = processor.feature_extractor.sampling_rate
audio = load_audio( "https://huggingface.co/datasets/hf-internal-testing/dummy-audio-samples/resolve/main/obama.mp3" ,
sampling_rate=sampling_rate,
)
first_chunk_inputs = processor(
audio[: processor.num_samples_first_audio_chunk],
sampling_rate=sampling_rate,
is_streaming= True ,
is_first_audio_chunk= True ,
language=language,
return_tensors= "pt" ,
)
first_chunk_inputs = first_chunk_inputs.to(model.device, dtype=model.dtype) def input_features_generator (): yield first_chunk_inputs.input_features[:, : processor.num_mel_frames_first_audio_chunk, :]
mel_frame_idx = processor.num_mel_frames_first_audio_chunk
hop_length = processor.feature_extractor.hop_length
n_fft = processor.feature_extractor.n_fft
start_idx = mel_frame_idx * hop_length - n_fft // 2 while (end_idx := start_idx + processor.num_samples_per_audio_chunk) < audio.shape[ 0 ]:
inputs = processor(
audio[start_idx:end_idx],
sampling_rate=sampling_rate,
is_streaming= True ,
is_first_audio_chunk= False ,
language=language,
return_tensors= "pt" ,
)
inputs = inputs.to(model.device, dtype=model.dtype) yield inputs.input_features
mel_frame_idx += processor.num_mel_frames_per_audio_chunk
start_idx = mel_frame_idx * hop_length - n_fft // 2 streamer = TextIteratorStreamer(processor.tokenizer, skip_special_tokens= True )
generate_kwargs = {
**first_chunk_inputs, "input_features" : input_features_generator(), "streamer" : streamer,
}
thread = Thread(target=model.generate, kwargs=generate_kwargs)
thread.start() print ( "Model output (streaming):" , end= " " , flush= True ) for text_chunk in streamer: print (text_chunk, end= "" , flush= True )
thread.join()
For more details about usage, please refer to the [Transformers documentation](<https://huggingface.co/docs/transformers/en/model_doc/nemotron3_5_asr>) .
Input(s):
Input Type(s): Audio, Lang ID
Input Format(s): wav, string
Input Parameters: One-Dimensional (1D) for audio and One-Dimensional (1D) for Lang ID
Other Properties Related to Input: Maximum Length in seconds specific to GPU Memory, No Pre-Processing Needed, Mono channel is required.
By leveraging NVIDIA’s hardware (e.g. GPU cores) and software frameworks (e.g., CUDA libraries), the model achieves faster training and inference times compared to CPU-only solutions.
Output
Output Type(s): Text String in Input Language
Output Format(s): String
Output Parameters: One-Dimensional (1D)
Other Properties Related to Output: No Maximum Character Length, transcribe punctuation and capitalization.
By leveraging NVIDIA’s hardware (e.g. GPU cores) and software frameworks (e.g., CUDA libraries), the model achieves faster training and inference times compared to CPU-only solutions.
Software Integration
Runtime Engine: NeMo 26.06
Supported Hardware Microarchitecture Compatibility:
NVIDIA Ampere
NVIDIA Blackwell
NVIDIA Hopper
NVIDIA Jetson
NVIDIA Lovelace
NVIDIA Turing
NVIDIA Volta
Supported Operating System(s):
Linux
Linux 4 Tegra
The integration of foundation and fine-tuned models into AI systems requires additional testing using use-case-specific data to ensure safe and effective deployment. Following the V-model methodology, iterative testing and validation at both unit and system levels are essential to mitigate risks, meet technical and functional requirements, and ensure compliance with safety and ethical standards before deployment.
Model Version(s):
nemotron-3.5-asr-streaming-0.6b-v1
Training and Evaluation Datasets:
Training Datasets
It was trained on speech data across 40 language-locales. The training data is a dynamic blend of public and proprietary internal datasets normalized to have spoken forms in text with punctuation and capitalization, including:
NVIDIA Riva multilingual ASR training set (Proprietary)
NVIDIA Granary [3]
Multilingual LibriSpeech (MLS)
Mozilla Common Voice
FLEURS
VoxPopuli / Europarl-ASR
** Data Modality: Audio
** Audio Training Data Size: 10,000 to 1 Million Hours
** Data Collection Method by dataset
Human
** Labeling Method by dataset
Human
Synthetic: Synthetic labels were generated from an ensemble of ASR models ( [NVIDIA Canary](<https://build.nvidia.com/nvidia/canary-1b-asr>) , [Parakeet Multilingual 1.1B RNNT](<https://build.nvidia.com/nvidia/parakeet-1_1b-rnnt-multilingual-asr>) , [Parakeet CTC 1.1B](<https://build.nvidia.com/nvidia/parakeet-ctc-1_1b-asr>) , [OpenAI Whisper](<https://huggingface.co/openai/whisper-large-v3>) , and [FunASR](<https://github.com/modelscope/FunASR>) ), with punctuation and capitalization (PnC) generated from [Qwen3-32B](<https://huggingface.co/Qwen/Qwen3-32B>) .
Evaluation Datasets
The model was evaluated on multilingual ASR benchmarks:
FLEURS
Mozilla Common Voice (MCV)
Multilingual LibriSpeech (MLS)
NVIDIA internal multilingual evaluation sets
** Data Collection Method by dataset
Human
** Labeling Method by dataset
Human
Performance
ASR performance is measured using the Word Error Rate (WER). The tables below report WER (%) on the FLEURS test sets across configurable streaming chunk sizes, in two modes:
Language Input (LangID): the target language is provided to the model.
Auto-detect: the model automatically detects the spoken language.
Note: Japanese, Korean, and Mandarin are evaluated using Character Error Rate (CER) rather than WER, as is standard for these languages. Note on text normalization: WER/CER are computed after text normalization that aligns the reference and hypothesis (e.g., casing, punctuation, numerals, and formatting conventions). Normalization is not perfect across all 40 language-locales, and residual mismatches between normalized text can inflate the reported error rates — actual transcription quality may be somewhat better than the numbers suggest.
Transcription-ready (19 locales)
Languages are ordered by accuracy (lowest WER first).
Language Language Input (LangID) Auto-detect
80ms 160ms 320ms 560ms 1.12s 80ms 160ms 320ms 560ms 1.12s
Spanish (es-US, es-ES) 4.87 4.64 4.39 4.26 4.11 5.04 4.82 4.48 4.34 4.13
Italian (it-IT) 5.23 4.85 4.83 4.41 4.25 5.28 4.89 4.84 4.47 4.32
Portuguese (pt-BR, pt-PT) 6.29 6.10 5.81 5.65 5.48 6.41 6.19 5.82 5.57 5.47
Hindi (hi-IN) 8.13 7.97 7.41 7.05 6.81 11.47 10.83 9.88 9.26 8.23
Korean (ko-KR) 7.59 7.70 7.27 7.18 7.12 8.31 8.18 7.81 7.49 7.30
English (en-US, en-GB) 9.43 8.88 8.27 7.99 7.91 9.72 9.34 8.84 8.80 8.84
German (de-DE) 9.81 9.21 8.83 8.42 8.31 9.90 9.37 8.87 8.58 8.22
French (fr-FR, fr-CA) 10.97 10.60 9.79 9.45 9.03 11.03 10.60 9.84 9.46 9.02
Russian (ru-RU) 10.84 10.73 9.87 9.60 9.17 12.47 12.09 11.01 10.57 10.03
Turkish (tr-TR) 12.34 12.33 12.05 11.34 11.17 12.61 12.28 11.93 11.51 11.32
Vietnamese (vi-VN) 13.41 12.87 12.29 11.78 11.18 13.59 13.02 12.40 12.02 11.22
Dutch (nl-NL) 14.03 13.43 12.17 11.97 11.46 14.09 13.80 12.62 12.24 11.70
Japanese (ja-JP) 13.87 12.90 12.22 11.91 11.48 14.97 13.85 13.00 12.38 11.66
Arabic (ar-AR) 13.17 12.65 12.55 12.13 12.03 13.47 12.85 12.67 12.18 12.06
Ukrainian (uk-UA) 15.70 15.21 14.55 13.67 13.07 18.81 17.96 16.79 15.60 14.59
Average 10.38 10.00 9.49 9.12 8.84 11.14 10.67 10.05 9.63 9.21
Broad-coverage (13 locales)
Languages are ordered by accuracy (lowest WER first).
Language Language Input (LangID) Auto-detect
80ms 160ms 320ms 560ms 1.12s 80ms 160ms 320ms 560ms 1.12s
Polish (pl-PL) 19.88 18.92 17.48 16.61 15.15 22.65 21.63 20.05 18.52 16.55
Norwegian Bokmål (nb-NO) 20.43 20.07 18.90 18.44 18.10 20.91 20.19 19.29 18.76 18.01
Finnish (fi-FI) 21.19 20.57 20.05 18.94 18.34 21.61 20.88 20.40 19.36 18.72
Mandarin (zh-CN) 20.56 20.22 20.03 19.51 19.28 22.45 21.07 20.59 20.40 19.87
Czech (cs-CZ) 24.18 23.20 22.41 21.04 20.41 25.81 25.12 23.68 22.55 21.45
Bulgarian (bg-BG) 24.50 23.58 22.80 21.70 20.53 28.28 27.22 25.54 24.05 21.84
Slovak (sk-SK) 25.08 24.14 23.73 22.51 21.28 27.59 26.06 25.61 24.15 22.68
Swedish (sv-SE) 25.61 24.85 23.63 22.72 22.17 26.28 25.56 24.18 23.57 22.53
Croatian (hr-HR) 27.92 27.09 25.79 24.92 23.97 32.13 31.20 29.65 28.95 27.46
Romanian (ro-RO) 31.52 30.93 29.04 27.77 25.90 34.22 33.26 30.97 29.84 26.88
Estonian (et-EE) 29.95 29.66 28.59 27.37 26.35 30.58 30.09 28.72 28.03 27.19
Danish (da-DK) 32.62 31.51 30.00 28.92 27.49 33.15 31.77 30.22 29.33 27.81
Hungarian (hu-HU) 32.70 32.03 30.92 29.72 28.68 33.40 32.39 31.49 30.20 29.18
Average 25.86 25.14 24.11 23.09 22.13 27.62 26.65 25.41 24.44 23.09
Adaptation-ready languages (fine-tune to enable)
These 8 language-locales are recognized by the tokenizer but are not tuned for production transcription out of the box: Greek (el-GR), Hebrew (he-IL), Lithuanian (lt-LT), Slovenian (sl-SI), Latvian (lv-LV), Maltese (mt-MT), Thai (th-TH), and Norwegian Nynorsk (nn-NO) . Fine-tuning on in-domain data is recommended to bring them to production quality.
License/Terms of Use
Governing Terms: Use of the model is governed by the [OpenMDW-1.1](<https://openmdw.ai/license/1-1/>) license.
Deployment Geography
Global
Use Case
This model is for transcription of multilingual audio.
References
[1] [Stateful Conformer with Cache-based Inference for Streaming Automatic Speech Recognition](<https://arxiv.org/abs/2312.17279>)
[2] [Fast Conformer with Linearly Scalable Attention for Efficient Speech Recognition](<https://arxiv.org/abs/2305.05084>)
[3] [NVIDIA Granary](<https://huggingface.co/datasets/nvidia/Granary>)
[4] [NVIDIA NeMo Framework](<https://github.com/NVIDIA/NeMo>)
Ethical Considerations
NVIDIA believes Trustworthy AI is a shared responsibility and we have established policies and practices to enable development for a wide array of AI applications. When downloaded or used in accordance with our terms of service, developers should work with their internal model team to ensure this model meets requirements for the relevant industry and use case and addresses unforeseen product misuse.
Please report model quality, risk, security vulnerabilities or NVIDIA AI Concerns [here](<https://www.nvidia.com/en-us/support/submit-security-vulnerability/>) .
Downloads last month
880,928
Safetensors
Model size
0.6B params
Tensor type
F32 ·
Model tree for nvidia/nemotron-3.5-asr-streaming-0.6b
Finetunes
[54 models](<https://huggingface.co/models?other=base_model:finetune:nvidia/nemotron-3.5-asr-streaming-0.6b>)
Quantizations
[56 models](<https://huggingface.co/models?other=base_model:quantized:nvidia/nemotron-3.5-asr-streaming-0.6b>)
Datasets used to train nvidia/nemotron-3.5-asr-streaming-0.6b
Spaces using nvidia/nemotron-3.5-asr-streaming-0.6b 37
Collection including nvidia/nemotron-3.5-asr-streaming-0.6b
[Open, state-of-the-art, production‑ready enterprise speech models from the NVIDIA Speech research team for ASR, TTS, Speaker Diarization and S2S • 15 items • Updated 11 days ago • 71](<https://huggingface.co/collections/nvidia/nemotron-speech>)
Papers for nvidia/nemotron-3.5-asr-streaming-0.6b
[Paper • 2312.17279 • Published Dec 27, 2023 • 4](<https://huggingface.co/papers/2312.17279>) [Paper • 2305.05084 • Published May 8, 2023 • 6](<https://huggingface.co/papers/2305.05084>)
Articles mentioning nvidia/nemotron-3.5-asr-streaming-0.6b
[**Know Who Spoke When: Build Real-Time, Multi-Speaker AI with NVIDIA Nemotron 3 Diarization** nvidia • 4 days ago • 57](<https://huggingface.co/blog/nvidia/nemotron-diarization>) [How to Fine-Tune Nemotron 3.5 ASR for Your Language, Domain, or Accent nvidia • Jun 4 • 79](<https://huggingface.co/blog/nvidia/fine-tuning-nemotron-35-asr>)
Evaluation results
[ARTPARK-IISc/Vaani-Benchmark-V1.0](<https://huggingface.co/datasets/ARTPARK-IISc/Vaani-Benchmark-V1.0>) · Hindi WER [View evaluation results](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/discussions/29>) [leaderboard](<https://huggingface.co/datasets/ARTPARK-IISc/Vaani-Benchmark-V1.0?eval_result=nvidia/nemotron-3.5-asr-streaming-0.6b&leaderboard_task_id=Hindi_WER>)
20.2
WER (1.12s frame size, LangID) on FLEURS (English)
[test set](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>)
7.910
WER (1.12s frame size, LangID) on FLEURS (Spanish)
[test set](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>)
4.110
WER (1.12s frame size, LangID) on FLEURS (French)
[test set](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>)
9.030
WER (1.12s frame size, LangID) on FLEURS (Italian)
[test set](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>)
4.250
WER (1.12s frame size, LangID) on FLEURS (Portuguese)
[test set](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>)
5.480
WER (1.12s frame size, LangID) on FLEURS (German)
[test set](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>)
8.310
WER (1.12s frame size, LangID) on FLEURS (Hindi)
[test set](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/blob/main/README.md>)
6.810
