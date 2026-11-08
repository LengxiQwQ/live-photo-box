using System;
using System.Collections.Generic;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Media.Models;

// Product callers consume Failure as data. Exception objects are retained only
// for compatibility with callers of the strict public media APIs.
internal sealed record NeutralPreparationAttempt(
    NeutralMediaBundle? Bundle, Exception? Failure,
    ProductOperationStage Stage = ProductOperationStage.Inspection,
    NeutralPreparationExecution? Execution = null)
{
    internal IReadOnlyList<MediaArtifact> RecoveryMedia { get; init; } = [];
    internal bool Succeeded => Bundle is not null && Failure is null;
    public static implicit operator NeutralPreparationAttempt(NeutralMediaBundle bundle) => new(bundle, null);
}
