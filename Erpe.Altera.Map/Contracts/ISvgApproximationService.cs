// <copyright file="ISvgApproximationService.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using NetTopologySuite.Geometries;

using Svg;

public interface ISvgApproximationService
{
    IEnumerable<CoordinateSequence> Approximate(SvgElement element);

    IEnumerable<Coordinate> GetCoordinates(SvgPath path);
}