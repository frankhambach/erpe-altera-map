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

    // The boundary of each given set of faces, walked as rings of the arcs it is made of. Every
    // arc with its set on both sides is dropped and every arc with it on one side is kept, so the
    // rings carry the arcs' own coordinates and nothing is recomputed. A ring of positive signed
    // area encloses ground, a negative one encloses a hole in it.
    //
    // All the sets at once rather than one call each, because the angular order of the half-edges
    // leaving every node is what the walk steers by and it depends on the graph alone. Built once
    // here; built per set it was four fifths of the time the whole phase took.
    IReadOnlyList<IReadOnlyList<DirectedArc[]>> DissolveFaces(
        ArcGraph arcGraph,
        IReadOnlyList<IReadOnlyCollection<Face>> sets);

    IEnumerable<Polygon> Polygonize(IReadOnlyCollection<LinearRing> linearRings);

    LinearRing Unloop(LinearRing linearRing);
}