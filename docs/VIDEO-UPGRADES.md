# Possible video upgrades

Current behavior and limits are in [media 7.8](07-media.md#78-video-metadata-and-available-captions). Metadata and available captions are implemented as optional evidence before judgment. The following are possibilities, not requirements or enabled features.

## Sampled frames

Use the existing image-capable judge with approximately three to five timestamped, ordered frames spread across a video, deduplicating nearly identical images. Budget frames separately from the post's original images. This can improve understanding of activities, scenes and on-screen text but misses short events and spoken content. A few image outputs do not guarantee a cheap download: seeking/range support differs by source. Evaluate a bounded low-resolution download or yt-dlp time sections through FFmpeg before committing to a retrieval design.

Derived frames need source identity, sampling-version and exact-byte provenance, a bounded private cache and explicit partial-coverage labels. A pre-judgment sampling budget must be separate from the existing post-verdict playback download. Never imply the entire video was reviewed.

## Speech transcription

When useful captions are unavailable, selectively fetch a bounded audio segment and transcribe locally, for example using [faster-whisper](https://github.com/SYSTRAN/faster-whisper). Preserve language, timestamps, automatic origin and coverage limits. This is likely more useful than more frames for lectures, news and spoken commentary. It adds audio retrieval, another runtime/model and CPU/GPU work; accuracy and throughput need measurement on the deployment hardware. Do not run it for every video by default.

## Temporal video models

A video-capable vision-language model, for example [Qwen3-VL](https://github.com/QwenLM/Qwen3-VL), can consume ordered sampled frames with temporal information and help with motion or events. A generic action classifier is not a replacement for applying the owner's written policy. Prefer a bounded evidence extraction stage feeding the existing policy judge, or compare direct video judgment with that baseline. Video vision does not automatically include audio understanding.

Assess model compatibility, memory, latency and concurrent workload on the actual server before installation. Start only if metadata/captions and a small frame sample demonstrably miss important decisions. Build an owner-reviewed evaluation set privately, comparing false hides, missed exclusions, evidence coverage and processing cost. No architecture choice eliminates ambiguous evidence or guarantees correct classification.

## Implementation references

- [yt-dlp options and metadata](https://github.com/yt-dlp/yt-dlp#readme): metadata JSON, available/automatic subtitles and FFmpeg-backed time sections.
- [FFmpeg documentation](https://ffmpeg.org/ffmpeg.html): seeking, video filters and frame extraction.

Future changes should remain optional, bounded, cancellation-aware and fail open on retrieval errors. Existing personal configuration and private video evidence must remain in the instance directory.
