// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

public interface ITopologyService
{
    // enclosedArea is what the faces have to come to, from ICoastlineService.AddArcs.
    void BuildFaces(ArcGraph arcGraph, double enclosedArea);

    LinearRing Close(CoordinateSequence coordinateSequence);

    IEnumerable<Polygon> Polygonize(IReadOnlyCollection<LinearRing> linearRings);

    LinearRing Unloop(LinearRing linearRing);
}