// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

public interface ITopologyService
{
    // enclosedArea is what the faces have to come to, from ICoastlineService.AddArcs.
    // dangleTolerance is how long an arc ending on a node with nothing else on it may be before
    // it is kept and reported rather than pruned as an overshoot. The weld tolerance, since
    // below that distance two drawn ends are already taken for one point.
    void BuildFaces(ArcGraph arcGraph, double enclosedArea, double dangleTolerance);

    LinearRing Close(CoordinateSequence coordinateSequence);

    IEnumerable<Polygon> Polygonize(IReadOnlyCollection<LinearRing> linearRings);

    LinearRing Unloop(LinearRing linearRing);
}