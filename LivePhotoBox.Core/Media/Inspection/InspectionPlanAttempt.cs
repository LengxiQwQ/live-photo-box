using System;

namespace LivePhotoBox.Media.Inspection;

internal sealed record InspectionPlanAttempt(InspectedSource? Source, Exception? Failure)
{
    internal bool Succeeded => Source is not null && Failure is null;
}
