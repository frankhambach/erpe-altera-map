// <copyright file="PointFExtensions.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

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