// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

// What one layer of the output looks like as a planar partition. Shared is the number that
// matters and UsedOnce is the one that proves it: see IVerificationService.
public sealed record SharingReport(
    int Segments,
    int Shared,
    int SameDirection,
    int OwnCountry,
    int UsedOnce,
    int Excess,
    int CoastSegments,
    int Unpaired)
{
    // What share of the boundary inland has only one side. Zero is the whole point.
    public double UnpairedShare => this.Segments == 0 ? 0.0 : this.Unpaired / (double)this.Segments;
}
