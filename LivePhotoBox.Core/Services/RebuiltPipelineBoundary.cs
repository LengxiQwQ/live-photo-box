using System;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>Hard boundary between the archived compatibility implementation and the new pipeline.</summary>
public static class RebuiltPipelineBoundary
{
    [Obsolete("Use ProcessingPipelineRouter at the operation boundary.")]
    public static void RequireLegacy(string operation)
    {
        ProcessingPipelineRouter.Begin(operation).EnsureLegacy();
    }
}

/// <summary>
/// Operations that are not yet rebuilt must fail before any Legacy implementation,
/// Native protocol helper, or output is used. Merge/split media conversion has its
/// own rebuilt entry points; this exception remains for the unfinished operations.
/// </summary>
public sealed class RebuiltPipelineNotReadyException : InvalidOperationException
{
    public string Operation { get; }

    public RebuiltPipelineNotReadyException(string operation)
        : base($"The rebuilt processing pipeline is enabled. '{operation}' is not implemented yet and no Legacy protocol fallback was used. Turn off the rebuilt-pipeline switch to use the preserved Legacy implementation.")
    {
        Operation = operation;
    }
}
