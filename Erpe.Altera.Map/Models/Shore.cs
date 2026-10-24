// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System.Collections.Generic;

using NetTopologySuite.Geometries;

// A coast or lake shore arc as the economic zones see it: the country whose land lies behind
// it, and the arc's full line in longitude and latitude, with the offset of a face taken round
// the rim already added. The offset is kept as well, for the points the
// zones unproject along the arc themselves. Every arc has one owner, because an arc ends wherever
// a border meets it, and that is what makes the country of a sample a property of the arc it was
// taken from.
//
// IsLandReversed is which way round the land face walks the arc. A face lies to the left of the
// arcs it is walked from, so the land is on the left of the arc walked that way and the water on
// the left of it walked the other.
public sealed class Shore(
    Country owner,
    Arc arc,
    IReadOnlyList<Coordinate> coordinates,
    double longitudeOffset,
    bool isLandReversed)
{
    public Arc Arc { get; } = arc;

    public bool IsLandReversed { get; } = isLandReversed;

    public IReadOnlyList<Coordinate> Coordinates { get; } = coordinates;

    public double LongitudeOffset { get; } = longitudeOffset;

    public Country Owner { get; } = owner;
}
