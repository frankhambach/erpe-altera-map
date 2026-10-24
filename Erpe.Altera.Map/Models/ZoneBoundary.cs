// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System.Collections.Generic;

using NetTopologySuite.Geometries;

// One stretch of the boundary of the economic zones, in longitude and latitude: a median line
// between two countries, Kind Median with both of them, or a stretch of the outer limit, Kind
// Limit with the one country whose zone it closes and no second. It runs between two points
// where something changes - three zones meeting, a median line reaching the limit - or closes
// on itself where nothing does.
//
// Left and Right are the same countries again, as the coordinates run, with longitude east and
// latitude north: the zone either side of the line. Null on the right of a limit is the high
// seas.
public sealed class ZoneBoundary(
    ArcKind kind,
    Country first,
    Country? second,
    Country left,
    Country? right,
    IReadOnlyList<Coordinate> coordinates)
{
    public IReadOnlyList<Coordinate> Coordinates { get; } = coordinates;

    public Country First { get; } = first;

    public ArcKind Kind { get; } = kind;

    public Country Left { get; } = left;

    public Country? Right { get; } = right;

    public Country? Second { get; } = second;
}
