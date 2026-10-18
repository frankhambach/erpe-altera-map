// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

using Serilog;

public sealed class AssemblyService : IAssemblyService
{
    private readonly GeometryFactory geometryFactory;

    private readonly ITopologyService topologyService;

    public AssemblyService(GeometryFactory geometryFactory, ITopologyService topologyService)
    {
        ArgumentNullException.ThrowIfNull(geometryFactory);
        ArgumentNullException.ThrowIfNull(topologyService);

        this.geometryFactory = geometryFactory;
        this.topologyService = topologyService;
    }

    // Replaces each country's drawn shapes with the faces it was assigned. The faces are
    // what the border network actually encloses, so from here on a border exists once and
    // both countries either side of it are built from the very same arc coordinates.
    public IReadOnlyList<Face> Assemble(ArcGraph arcGraph, IEnumerable<Country> countries)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(countries);

        Dictionary<Country, List<Face>> byOwner = [];
        foreach (Face face in arcGraph.Faces.Where(face => face.Owner != null))
        {
            if (!byOwner.TryGetValue(face.Owner!, out List<Face>? owned))
            {
                owned = [];
                byOwner.Add(face.Owner!, owned);
            }

            owned.Add(face);
        }

        // A country holds several faces wherever a substate border cuts through it, so the faces
        // are dissolved back together - by walking the arcs that bound them rather than by
        // unioning their geometry. The arcs that divide one of its own faces from another drop
        // out, and what is left is strung into rings of the arcs' own coordinates. Nothing is
        // recomputed, so a border cannot come out different from the one the neighbour on the far
        // side is built from.
        //
        // Every country in one call, because the walk shares one index over the whole graph.
        Country[] owners = [.. countries];
        IReadOnlyList<IReadOnlyList<DirectedArc[]>> dissolved = this.topologyService.DissolveFaces(
            arcGraph,
            [.. owners.Select(country => byOwner.TryGetValue(country, out List<Face>? owned) ? owned : [])]);

        // Which faces were lost can no longer be pinned on one of them: a country's boundary is
        // now walked as a whole, so an invalid lobe belongs to the region and not to a face of it.
        // What the caller needs this for is the allowance in 7 for boundary with one side to it,
        // and giving it the whole country's faces keeps that allowance conservative. Nothing on
        // this map reaches it - no country's boundary comes out invalid.
        HashSet<Face> dropped = [];
        double droppedArea = 0.0;
        int assembled = 0;
        for (int i = 0; i < owners.Length; ++i)
        {
            Country country = owners[i];
            country.Shapes.Clear();
            (Polygon Polygon, DirectedArc[][] Rings)[] shapes = [.. this.CreateShapes(dissolved[i])];
            foreach ((Polygon polygon, DirectedArc[][] _) in shapes.Where(
                         shape => !shape.Polygon.IsEmpty && !shape.Polygon.IsValid))
            {
                droppedArea += Math.Abs(polygon.Area);
                dropped.UnionWith(byOwner[country]);
            }

            foreach (((Polygon polygon, DirectedArc[][] rings), int index) in shapes
                         .Where(shape => !shape.Polygon.IsEmpty && shape.Polygon.IsValid)
                         .Select((shape, index) => (shape, index)))
            {
                country.Shapes.Add(new Shape($"{country.Id}-{index}", polygon, rings));
                ++assembled;
            }
        }

        Log.Information(
            "Assembled {Shapes} shapes for {Countries} countries by walking the arcs that bound their faces.",
            assembled,
            byOwner.Count);

        if (dropped.Count > 0)
        {
            Log.Warning(
                "{Dropped} faces belong to a boundary that crosses itself, covering {Area:0.0} square units, and "
                + "were left out.",
                dropped.Count,
                droppedArea);
        }

        return [.. dropped];
    }

    // Cuts a ring of arcs wherever it comes back to a node it has already been through, which is
    // where the region is pinched to a point or a hole touches its outside. That is one ring in
    // the graph but it crosses itself as geometry, and NetTopologySuite rejects it.
    //
    // Cut by node rather than by coordinate, which is what this used to do, so that each lobe is
    // still a run of whole arcs and can be written out as one. The two are the same thing here:
    // 3 of the 7,244 dissolved rings on this map need cutting and all 3 pinch at a node, which is
    // what 5a guarantees for a crossing and 2a for a tracer touching itself. A pinch
    // somewhere inside an arc would be missed, and the polygon it makes would then fail the
    // validity check below and be reported rather than quietly written.
    private static List<DirectedArc[]> UnloopArcs(DirectedArc[] ring)
    {
        List<DirectedArc[]> loops = [];
        List<DirectedArc> current = [];
        Dictionary<Node, int> seen = [];
        foreach (DirectedArc directed in ring)
        {
            if (seen.TryGetValue(directed.StartNode, out int start))
            {
                loops.Add([.. current.GetRange(start, current.Count - start)]);
                for (int i = start; i < current.Count; ++i)
                {
                    seen.Remove(current[i].StartNode);
                }

                current.RemoveRange(start, current.Count - start);
            }

            seen[directed.StartNode] = current.Count;
            current.Add(directed);
        }

        if (current.Count > 0)
        {
            loops.Add([.. current]);
        }

        return loops;
    }

    // Each shape as both the geometry and the arcs it is made of, so that the one can be written
    // as GeoJson and the other as a topology without either being recovered from the other.
    //
    // Which rings are ground and which are holes in it comes from the walk: a ring of positive
    // signed area encloses ground, a negative one a hole. Cutting can turn one ring into several,
    // so the sign is taken from each lobe rather than from the ring it came out of.
    private IEnumerable<(Polygon Polygon, DirectedArc[][] Rings)> CreateShapes(
        IReadOnlyList<DirectedArc[]> rings)
    {
        List<(LinearRing Ring, DirectedArc[] Arcs)> shells = [];
        List<(LinearRing Ring, DirectedArc[] Arcs)> holes = [];
        foreach (DirectedArc[] ring in rings)
        {
            foreach (DirectedArc[] lobe in UnloopArcs(ring))
            {
                LinearRing? closed = this.CreateRing(lobe.GetCoordinates());
                if (closed == null)
                {
                    continue;
                }

                if (closed.Coordinates.GetSignedArea() > 0.0)
                {
                    shells.Add((closed, lobe));
                }
                else
                {
                    holes.Add((closed, lobe));
                }
            }
        }

        if (shells.Count == 0)
        {
            return [];
        }

        // With one shell every hole belongs to it. With several, each hole goes to the smallest
        // shell that encloses it, and one no shell encloses is left out, exactly as before.
        List<(LinearRing Ring, DirectedArc[] Arcs)>[] byLobe = [.. shells.Select(_ =>
            new List<(LinearRing Ring, DirectedArc[] Arcs)>())];
        if (shells.Count == 1)
        {
            byLobe[0].AddRange(holes);
        }
        else
        {
            Polygon[] lobes = [.. shells.Select(shell => this.geometryFactory.CreatePolygon(shell.Ring))];
            foreach ((LinearRing hole, DirectedArc[] arcs) in holes)
            {
                int best = -1;
                for (int i = 0; i < lobes.Length; ++i)
                {
                    if (lobes[i].Contains(this.geometryFactory.CreatePoint(hole.GetCoordinateN(0)))
                        && ((best < 0) || (lobes[i].Area < lobes[best].Area)))
                    {
                        best = i;
                    }
                }

                if (best >= 0)
                {
                    byLobe[best].Add((hole, arcs));
                }
            }
        }

        return shells.Select(
            (shell, i) => (
                this.geometryFactory.CreatePolygon(shell.Ring, [.. byLobe[i].Select(hole => hole.Ring)]),
                (DirectedArc[][])[shell.Arcs, .. byLobe[i].Select(hole => hole.Arcs)]));
    }

    private LinearRing? CreateRing(List<Coordinate> coordinates)
    {
        if (coordinates.Count < 3)
        {
            return null;
        }

        List<Coordinate> closed = [.. coordinates, coordinates[0]];

        return this.geometryFactory.CreateLinearRing(closed.ToArray());
    }
}
