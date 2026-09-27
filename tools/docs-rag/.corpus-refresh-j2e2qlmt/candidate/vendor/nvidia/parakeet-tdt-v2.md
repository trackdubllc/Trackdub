Source: https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2

🦜 Parakeet TDT 0.6B V2 (En)
| |
🎉 NEW: Multilingual Parakeet TDT 0.6B V3 is now available!
🌍 25 European Languages | 🚀 Enhanced Performance | 🔗 [Try it here: nvidia/parakeet-tdt-0.6b-v3](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3>)
Description:
parakeet-tdt-0.6b-v2 is a 600-million-parameter automatic speech recognition (ASR) model designed for high-quality English transcription, featuring support for punctuation, capitalization, and accurate timestamp prediction. Try Demo here: [https://huggingface.co/spaces/nvidia/parakeet-tdt-0.6b-v2](<https://huggingface.co/spaces/nvidia/parakeet-tdt-0.6b-v2>)
This XL variant of the FastConformer [1] architecture integrates the TDT [2] decoder and is trained with full attention, enabling efficient transcription of audio segments up to 24 minutes in a single pass. The model achieves an RTFx of 3380 on the HF-Open-ASR leaderboard with a batch size of 128. Note: RTFx Performance may vary depending on dataset audio duration and batch size.
Key Features
Accurate word-level timestamp predictions
Automatic punctuation and capitalization
Robust performance on spoken numbers, and song lyrics transcription
For more information, refer to the Model Architecture section and the [NeMo documentation](<https://docs.nvidia.com/deeplearning/nemo/user-guide/docs/en/main/asr/models.html#fast-conformer>) .
This model is ready for commercial/non-commercial use.
License/Terms of Use:
GOVERNING TERMS: Use of this model is governed by the [CC-BY-4.0](<https://creativecommons.org/licenses/by/4.0/legalcode.en>) license.
Discover more from NVIDIA:
For documentation, deployment guides, enterprise-ready APIs, and the latest open models—including Nemotron and other cutting-edge speech, translation, and generative AI—visit the NVIDIA Developer Portal at developer.nvidia.com.
Join the community to access tools, support, and resources to accelerate your development with NVIDIA’s NeMo, Riva, NIM, and foundation models.
Explore more from NVIDIA:
What is [Nemotron](<https://www.nvidia.com/en-us/ai-data-science/foundation-models/nemotron/>) ?
NVIDIA Developer [Nemotron](<https://developer.nvidia.com/nemotron>)
[NVIDIA Riva Speech](<https://developer.nvidia.com/riva?sortBy=developer_learning_library%2Fsort%2Ffeatured_in.riva%3Adesc%2Ctitle%3Aasc#demos>)
[NeMo Documentation](<https://docs.nvidia.com/nemo-framework/user-guide/latest/nemotoolkit/asr/models.html>)
Deployment Geography:
Global
Use Case:
This model serves developers, researchers, academics, and industries building applications that require speech-to-text capabilities, including but not limited to: conversational AI, voice assistants, transcription services, subtitle generation, and voice analytics platforms.
Release Date:
05/01/2025
Model Architecture:
Architecture Type :
FastConformer-TDT
Network Architecture :
This model was developed based on [FastConformer encoder](<https://docs.nvidia.com/deeplearning/nemo/user-guide/docs/en/main/asr/models.html#fast-conformer>) architecture[1] and TDT decoder[2]
This model has 600 million model parameters.
Input:
Input Type(s): 16kHz Audio
Input Format(s): .wav and .flac audio formats
Input Parameters: 1D (audio signal)
Other Properties Related to Input: Monochannel audio
Output:
Output Type(s): Text
Output Format: String
Output Parameters: 1D (text)
Other Properties Related to Output: Punctuations and Capitalizations included.
Our AI models are designed and/or optimized to run on NVIDIA GPU-accelerated systems. By leveraging NVIDIA's hardware (e.g. GPU cores) and software frameworks (e.g., CUDA libraries), the model achieves faster training and inference times compared to CPU-only solutions.
How to Use this Model:
To train, fine-tune or play with the model you will need to install [NVIDIA NeMo](<https://github.com/NVIDIA/NeMo>) . We recommend you install it after you've installed latest PyTorch version.
pip install -U nemo_toolkit[ "asr" ]
The model is available for use in the NeMo toolkit [3], and can be used as a pre-trained checkpoint for inference or for fine-tuning on another dataset.
Automatically instantiate the model
import nemo.collections.asr as nemo_asr
asr_model = nemo_asr.models.ASRModel.from_pretrained(model_name= "nvidia/parakeet-tdt-0.6b-v2" )
Transcribing using Python
First, let's get a sample
wget https://dldata-public.s3.us-east-2.amazonaws.com/2086-149220-0033.wav
Then simply do:
output = asr_model.transcribe([ '2086-149220-0033.wav' ]) print (output[ 0 ].text)
Transcribing with timestamps
To transcribe with timestamps:
output = asr_model.transcribe([ '2086-149220-0033.wav' ], timestamps= True ) # by default, timestamps are enabled for char, word and segment level word_timestamps = output[ 0 ].timestamp[ 'word' ] # word level timestamps for first sample segment_timestamps = output[ 0 ].timestamp[ 'segment' ] # segment level timestamps char_timestamps = output[ 0 ].timestamp[ 'char' ] # char level timestamps for stamp in segment_timestamps: print ( f" {stamp[ 'start' ]} s - {stamp[ 'end' ]} s : {stamp[ 'segment' ]} " )
Try via API — No Setup Required
Transcribe audio instantly using the free hosted API on [build.nvidia.com](<https://build.nvidia.com/nvidia/parakeet-tdt-0_6b-v2>) — no GPU, no Docker, no model download needed.
1. Get a free API key: Visit [build.nvidia.com/nvidia/parakeet-tdt-0_6b-v2](<https://build.nvidia.com/nvidia/parakeet-tdt-0_6b-v2>) and click Get API Key
2. Install the Riva client:
pip install nvidia-riva-client
3. Transcribe an audio file:
import riva.client
auth = riva.client.Auth(
uri= "grpc.nvcf.nvidia.com:443" ,
use_ssl= True ,
metadata_args=[
[ "function-id" , "d3fe9151-442b-4204-a70d-5fcc597fd610" ],
[ "authorization" , "Bearer nvapi-YOUR_API_KEY" ]
]
)
asr_service = riva.client.ASRService(auth) with open ( "audio.wav" , "rb" ) as f:
audio = f.read()
config = riva.client.RecognitionConfig(
language_code= "en-US" ,
max_alternatives= 1 ,
enable_automatic_punctuation= True ,
enable_word_time_offsets= True ,
)
response = asr_service.offline_recognize(audio, config) print (response.results[ 0 ].alternatives[ 0 ].transcript)
Or use the CLI:
git clone https://github.com/nvidia-riva/python-clients.git export NVIDIA_API_KEY= "nvapi-YOUR_API_KEY" python python-clients/scripts/asr/transcribe_file_offline.py \
--server grpc.nvcf.nvidia.com:443 --use-ssl \
--metadata function-id "d3fe9151-442b-4204-a70d-5fcc597fd610" \
--metadata "authorization" "Bearer $NVIDIA_API_KEY " \
--language-code en-US \
--word-time-offsets --automatic-punctuation \
--input-file audio.wav
Note: The hosted API accepts 16-bit mono audio in WAV, OGG, or OPUS format. See the [API Reference](<https://docs.nvidia.com/nim/riva/asr/latest/protos.html>) for streaming and advanced options.
Software Integration:
Runtime Engine(s):
NeMo 2.2
Supported Hardware Microarchitecture Compatibility:
NVIDIA Ampere
NVIDIA Blackwell
NVIDIA Hopper
NVIDIA Volta
[Preferred/Supported] Operating System(s):
Linux
Hardware Specific Requirements:
Atleast 2GB RAM for model to load. The bigger the RAM, the larger audio input it supports.
Model Version
Current version: parakeet-tdt-0.6b-v2. Previous versions can be [accessed](<https://huggingface.co/collections/nvidia/parakeet-659711f49d1469e51546e021>) here.
Training and Evaluation Datasets:
Training
This model was trained using the NeMo toolkit [3], following the strategies below:
Initialized from a FastConformer SSL checkpoint that was pretrained with a wav2vec method on the LibriLight dataset[7].
Trained for 150,000 steps on 64 A100 GPUs.
Dataset corpora were balanced using a temperature sampling value of 0.5.
Stage 2 fine-tuning was performed for 2,500 steps on 4 A100 GPUs using approximately 500 hours of high-quality, human-transcribed data of NeMo ASR Set 3.0.
Training was conducted using this [example script](<https://github.com/NVIDIA/NeMo/blob/main/examples/asr/asr_transducer/speech_to_text_rnnt_bpe.py>) and [TDT configuration](<https://github.com/NVIDIA/NeMo/blob/main/examples/asr/conf/fastconformer/hybrid_transducer_ctc/fastconformer_hybrid_tdt_ctc_bpe.yaml>) .
The tokenizer was constructed from the training set transcripts using this [script](<https://github.com/NVIDIA/NeMo/blob/main/scripts/tokenizers/process_asr_text_tokenizer.py>) .
Training Dataset
The model was trained on the Granary dataset[8], consisting of approximately 120,000 hours of English speech data:
10,000 hours from human-transcribed NeMo ASR Set 3.0, including:
LibriSpeech (960 hours)
Fisher Corpus
National Speech Corpus Part 1
VCTK
VoxPopuli (English)
Europarl-ASR (English)
Multilingual LibriSpeech (MLS English) – 2,000-hour subset
Mozilla Common Voice (v7.0)
AMI
110,000 hours of pseudo-labeled data from:
YTC (YouTube-Commons) dataset[4]
YODAS dataset [5]
Librilight [7]
All transcriptions preserve punctuation and capitalization. The Granary dataset[8] will be made publicly available after presentation at Interspeech 2025.
Data Collection Method by dataset
Hybrid: Automated, Human
Labeling Method by dataset
Hybrid: Synthetic, Human
Properties:
Noise robust data from various sources
Single channel, 16kHz sampled data
Evaluation Dataset
Huggingface Open ASR Leaderboard datasets are used to evaluate the performance of this model.
Data Collection Method by dataset
Human
Labeling Method by dataset
Human
Properties:
All are commonly used for benchmarking English ASR systems.
Audio data is typically processed into a 16kHz mono channel format for ASR evaluation, consistent with benchmarks like the [Open ASR Leaderboard](<https://huggingface.co/spaces/hf-audio/open_asr_leaderboard>) .
Performance
Huggingface Open-ASR-Leaderboard Performance
The performance of Automatic Speech Recognition (ASR) models is measured using Word Error Rate (WER). Given that this model is trained on a large and diverse dataset spanning multiple domains, it is generally more robust and accurate across various types of audio.
Base Performance
The table below summarizes the WER (%) using a Transducer decoder with greedy decoding (without an external language model):
Model Avg WER AMI Earnings-22 GigaSpeech LS test-clean LS test-other SPGI Speech TEDLIUM-v3 VoxPopuli
parakeet-tdt-0.6b-v2 6.05 11.16 11.15 9.74 1.69 3.19 2.17 3.38 5.95
Noise Robustness
Performance across different Signal-to-Noise Ratios (SNR) using MUSAN music and noise samples:
SNR Level Avg WER AMI Earnings GigaSpeech LS test-clean LS test-other SPGI Tedlium VoxPopuli Relative Change
Clean 6.05 11.16 11.15 9.74 1.69 3.19 2.17 3.38 5.95 -
SNR 10 6.95 14.38 12.04 10.24 1.92 4.13 2.84 3.63 6.38 -14.75%
SNR 5 8.23 18.07 13.82 11.18 2.33 5.58 3.81 4.24 6.81 -35.97%
SNR 0 11.88 25.43 18.59 14.32 4.40 10.07 7.27 6.42 8.54 -96.28%
SNR -5 20.26 36.57 28.06 22.27 11.82 19.91 16.14 13.07 14.23 -234.66%
Telephony Audio Performance
Performance comparison between standard 16kHz audio and telephony-style audio (using μ-law encoding with 16kHz→8kHz→16kHz conversion):
Audio Format Avg WER AMI Earnings GigaSpeech LS test-clean LS test-other SPGI Tedlium VoxPopuli Relative Change
Standard 16kHz 6.05 11.16 11.15 9.74 1.69 3.19 2.17 3.38 5.95 -
μ-law 8kHz 6.32 11.98 11.16 10.02 1.78 3.52 2.20 3.38 6.52 -4.10%
These WER scores were obtained using greedy decoding without an external language model. Additional evaluation details are available on the [Hugging Face ASR Leaderboard](<https://huggingface.co/spaces/hf-audio/open_asr_leaderboard>) .[6]
References
[1] [Fast Conformer with Linearly Scalable Attention for Efficient Speech Recognition](<https://arxiv.org/abs/2305.05084>)
[2] [Efficient Sequence Transduction by Jointly Predicting Tokens and Durations](<https://arxiv.org/abs/2304.06795>)
[3] [NVIDIA NeMo Toolkit](<https://github.com/NVIDIA/NeMo>)
[4] [Youtube-commons: A massive open corpus for conversational and multimodal data](<https://huggingface.co/blog/Pclanglais/youtube-commons>)
[5] [Yodas: Youtube-oriented dataset for audio and speech](<https://arxiv.org/abs/2406.00899>)
[6] [HuggingFace ASR Leaderboard](<https://huggingface.co/spaces/hf-audio/open_asr_leaderboard>)
[7] [MOSEL: 950,000 Hours of Speech Data for Open-Source Speech Foundation Model Training on EU Languages](<https://arxiv.org/abs/2410.01036>)
[8] [Granary: Speech Recognition and Translation Dataset in 25 European Languages](<https://arxiv.org/pdf/2505.13404>)
Inference:
Engine :
NVIDIA NeMo
Test Hardware :
NVIDIA A10
NVIDIA A100
NVIDIA A30
NVIDIA H100
NVIDIA L4
NVIDIA L40
NVIDIA Turing T4
NVIDIA Volta V100
Ethical Considerations:
NVIDIA believes Trustworthy AI is a shared responsibility and we have established policies and practices to enable development for a wide array of AI applications. When downloaded or used in accordance with our terms of service, developers should work with their supporting model team to ensure this model meets requirements for the relevant industry and use case and addresses unforeseen product misuse.
For more detailed information on ethical considerations for this model, please see the Model Card++ Explainability, Bias, Safety & Security, and Privacy Subcards [here](<https://developer.nvidia.com/blog/enhancing-ai-transparency-and-ethical-considerations-with-model-card/>) .
Please report security vulnerabilities or NVIDIA AI Concerns [here](<https://www.nvidia.com/en-us/support/submit-security-vulnerability/>) .
Bias:
Field Response
Participation considerations from adversely impacted groups [protected classes](<https://www.senate.ca.gov/content/protected-classes>) in model design and testing None
Measures taken to mitigate against unwanted bias None
Explainability:
Field Response
Intended Domain Speech to Text Transcription
Model Type FastConformer
Intended Users This model is intended for developers, researchers, academics, and industries building conversational based applications.
Output Text
Describe how the model works Speech input is encoded into embeddings and passed into conformer-based model and output a text response.
Name the adversely impacted groups this has been tested to deliver comparable outcomes regardless of Not Applicable
Technical Limitations & Mitigation Transcripts may be not 100% accurate. Accuracy varies based on language and characteristics of input audio (Domain, Use Case, Accent, Noise, Speech Type, Context of speech, etc.)
Verified to have met prescribed NVIDIA quality standards Yes
Performance Metrics Word Error Rate
Potential Known Risks If a word is not trained in the language model and not presented in vocabulary, the word is not likely to be recognized. Not recommended for word-for-word/incomplete sentences as accuracy varies based on the context of input text
Licensing GOVERNING TERMS: Use of this model is governed by the [CC-BY-4.0](<https://creativecommons.org/licenses/by/4.0/legalcode.en>) license.
Privacy:
Field Response
Generatable or reverse engineerable personal data? None
Personal data used to create this model? None
Is there provenance for all datasets used in training? Yes
Does data labeling (annotation, metadata) comply with privacy laws? Yes
Is data compliant with data subject requests for data correction or removal, if such a request was made? No, not possible with externally-sourced data.
Applicable Privacy Policy [https://www.nvidia.com/en-us/about-nvidia/privacy-policy/](<https://www.nvidia.com/en-us/about-nvidia/privacy-policy/>)
Safety:
Field Response
Model Application(s) Speech to Text Transcription
Describe the life critical impact None
Use Case Restrictions Abide by [CC-BY-4.0](<https://creativecommons.org/licenses/by/4.0/legalcode.en>) License
Model and dataset restrictions The Principle of least privilege (PoLP) is applied limiting access for dataset generation and model development. Restrictions enforce dataset access during training, and dataset license constraints adhered to.
Downloads last month
193,528
Model tree for nvidia/parakeet-tdt-0.6b-v2
Adapters
[6 models](<https://huggingface.co/models?other=base_model:adapter:nvidia/parakeet-tdt-0.6b-v2>)
Finetunes
[46 models](<https://huggingface.co/models?other=base_model:finetune:nvidia/parakeet-tdt-0.6b-v2>)
Merges
[1 model](<https://huggingface.co/models?other=base_model:merge:nvidia/parakeet-tdt-0.6b-v2>)
Quantizations
[27 models](<https://huggingface.co/models?other=base_model:quantized:nvidia/parakeet-tdt-0.6b-v2>)
Dataset used to train nvidia/parakeet-tdt-0.6b-v2
Spaces using nvidia/parakeet-tdt-0.6b-v2 56
Collection including nvidia/parakeet-tdt-0.6b-v2
[NeMo Parakeet ASR Models attain strong speech recognition accuracy while being efficient for inference. Available in CTC and RNN-Transducer variants. • 16 items • Updated Aug 11 • 84](<https://huggingface.co/collections/nvidia/parakeet-asr>)
Papers for nvidia/parakeet-tdt-0.6b-v2
[Paper • 2505.13404 • Published May 19, 2025 • 4](<https://huggingface.co/papers/2505.13404>) [Paper • 2410.01036 • Published Oct 1, 2024 • 16](<https://huggingface.co/papers/2410.01036>) [Paper • 2406.00899 • Published Jun 2, 2024 • 5](<https://huggingface.co/papers/2406.00899>) [Paper • 2305.05084 • Published May 8, 2023 • 6](<https://huggingface.co/papers/2305.05084>) [Paper • 2304.06795 • Published Apr 13, 2023 • 2](<https://huggingface.co/papers/2304.06795>)
Evaluation results
[hf-audio/open-asr-leaderboard](<https://huggingface.co/datasets/hf-audio/open-asr-leaderboard>) [leaderboard](<https://huggingface.co/datasets/hf-audio/open-asr-leaderboard?eval_result=nvidia/parakeet-tdt-0.6b-v2>)
Mean Wer [View evaluation results](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/blob/main/.eval_results/open_asr_leaderboard.yaml>) [source](<https://huggingface.co/hf-audio>)
6.05
Rtfx [View evaluation results](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/blob/main/.eval_results/open_asr_leaderboard.yaml>) [source](<https://huggingface.co/hf-audio>)
3,386.02
Ami Wer [View evaluation results](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/blob/main/.eval_results/open_asr_leaderboard.yaml>) [source](<https://huggingface.co/hf-audio>)
11.16
Test WER on AMI (Meetings test)
[test set](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/blob/main/README.md>)
11.160
Test WER on Earnings-22
[test set](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/blob/main/README.md>)
11.150
Test WER on GigaSpeech
[test set](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/blob/main/README.md>) [self-reported](<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/blob/main/README.md>)
9.740
