// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System.Drawing;

using NetTopologySuite.Geometries;

public static class PointFExtensions
{
    public static Coordinate ToCoordinate(this PointF point)
    {
        return new Coordinate(point.X, point.Y);
    }
}