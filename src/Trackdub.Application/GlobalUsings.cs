global using Trackdub.Application.ModelOptimization;
global using Trackdub.Application.Projects;
global using Trackdub.Application.Transcripts;
global using Trackdub.Contracts;
global using Trackdub.Contracts.ApplicationContracts;
global using Trackdub.Contracts.Licensing;
global using Trackdub.Contracts.ModelOptimization;
global using Trackdub.Contracts.Pipeline;
global using Trackdub.Contracts.Projects;
global using Trackdub.Contracts.Transcripts;

// Prefer domain transcript models over legacy DTOs in Trackdub.Contracts root.
global using TranscriptSegment = Trackdub.Domain.Transcript.TranscriptSegment;
global using TranslatedSegment = Trackdub.Domain.Translation.TranslatedSegment;
