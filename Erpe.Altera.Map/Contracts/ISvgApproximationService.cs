// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using NetTopologySuite.Geometries;

using Svg;

public interface ISvgApproximationService
{
    IEnumerable<CoordinateSequence> Approximate(SvgElement element);

    IEnumerable<Coordinate> GetCoordinates(SvgPath path);
}