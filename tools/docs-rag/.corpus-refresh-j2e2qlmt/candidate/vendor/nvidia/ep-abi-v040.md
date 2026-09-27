Source: https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/releases/tag/v0.4.0

[NVIDIA](<https://github.com/NVIDIA>) / [TensorRT-RTX-EP-ABI](<https://github.com/NVIDIA/TensorRT-RTX-EP-ABI>) Public
[Notifications](<https://github.com/login?return_to=%2FNVIDIA%2FTensorRT-RTX-EP-ABI>) You must be signed in to change notification settings
[Fork 6](<https://github.com/login?return_to=%2FNVIDIA%2FTensorRT-RTX-EP-ABI>)
[Star 31](<https://github.com/login?return_to=%2FNVIDIA%2FTensorRT-RTX-EP-ABI>)
v0.4.0
Choose a tag to compare
Sorry, something went wrong.
Filter Loading
Sorry, something went wrong.
Uh oh!
There was an error while loading. Please reload this page .
No results found
[View all tags](<https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/tags>)
[umangb-09](<https://github.com/umangb-09>) released this 12 Aug 10:45
· [4 commits](<https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/compare/v0.4.0...main>) to main
since this release
[v0.4.0](<https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/tree/v0.4.0>)
[7bc9add](<https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/commit/7bc9addc928590802c4d95589ac3caa07a9cf517>)
This commit was created on GitHub.com and signed with GitHub’s verified signature .
GPG key ID: B5690EEEBB952194
Verified
[Learn about vigilant mode](<https://docs.github.com/github/authenticating-to-github/displaying-verification-statuses-for-all-of-your-commits>) .
Bug fixes and improvements
Add opt-in synchronous GPU allocator (nv_use_sync_gpu_allocator) with a Technical Notes doc
Support iGPU zero-copy: pass resolved device pointers from host-accessible tensors directly to TRT context
Enable Vulkan resource import and CiG; add D3D12 external resource import, disable CPU fallback
Add compile-only mode support for ORT 1.27+ (EP ABI changes to reduce compile time)
Pass Phi-4 LongRoPE cache offset through the EP
Add IHV Weightless EP Context: generate EP context models without embedded weights
Add profiler support for MSFT integration
Add Windows-on-Arm binary and wheel build support in build.bat
Fix OOB write on high-rank inputs and use-after-free in EP destructor
Pin EP DLL in DllMain throughout ORT process lifetime to prevent premature unload
Fix path traversal via model-supplied name in runtime cache; remove legacy RefitEngineImpl fallback
Fix runtime cache teardown lifetime; use MinSupportedOrtApiVersion for ORT version comparison
Contributors to this release of TensorRT RTX EP ABI:
[@keshavv27](<https://github.com/keshavv27>) , [@gedoensmax](<https://github.com/gedoensmax>) , [@ishwar-raut1](<https://github.com/ishwar-raut1>) , [@umangb-09](<https://github.com/umangb-09>) , [@yen-shi](<https://github.com/yen-shi>) , [@praneshgo](<https://github.com/praneshgo>) , [@wenbingl](<https://github.com/wenbingl>) , [@mklimenko-nv](<https://github.com/mklimenko-nv>) , [@hthadicherla](<https://github.com/hthadicherla>) , [@sandeep-nv](<https://github.com/sandeep-nv>)
Contributors
yen-shi, wenbingl, and 8 other contributors
Assets 8 Loading Uh oh!
There was an error while loading. Please reload this page .
