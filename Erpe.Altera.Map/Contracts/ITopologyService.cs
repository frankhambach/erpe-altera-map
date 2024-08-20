// <copyright file="ITopologyService.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using NetTopologySuite.Geometries;

public interface ITopologyService
{
    LinearRing Close(LineString lineString);

    IEnumerable<Polygon> Polygonize(IReadOnlyCollection<LinearRing> linearRings);

    LinearRing Unloop(LinearRing linearRing);
}