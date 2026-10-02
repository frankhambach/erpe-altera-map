// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

public interface ITopologyService
{
    void BuildFaces(ArcGraph arcGraph);

    LinearRing Close(CoordinateSequence coordinateSequence);

    IEnumerable<Polygon> Polygonize(IReadOnlyCollection<LinearRing> linearRings);

    LinearRing Unloop(LinearRing linearRing);
}