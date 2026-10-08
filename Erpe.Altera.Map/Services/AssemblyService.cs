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
            Polygon[] polygons = [.. this.CreatePolygons(dissolved[i])];
            foreach (Polygon polygon in polygons.Where(polygon => !polygon.IsEmpty && !polygon.IsValid))
            {
                droppedArea += Math.Abs(polygon.Area);
                dropped.UnionWith(byOwner[country]);
            }

            foreach ((Polygon polygon, int index) in polygons
                         .Where(polygon => !polygon.IsEmpty && polygon.IsValid)
                         .Select((polygon, index) => (polygon, index)))
            {
                country.Shapes.Add(new Shape($"{country.Id}-{index}", polygon));
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

    // The same shoelace 4b sorts its rings by, rather than a library's idea of winding, so that
    // ground and hole mean here exactly what they mean there.
    private static double GetSignedArea(LinearRing ring)
    {
        Coordinate[] coordinates = ring.Coordinates;
        double area = 0.0;
        for (int i = 0; i < (coordinates.Length - 1); ++i)
        {
            area += (coordinates[i].X * coordinates[i + 1].Y) - (coordinates[i + 1].X * coordinates[i].Y);
        }

        return area / 2.0;
    }

    // A dissolved boundary may come back to a node it has already been through, where the region
    // is pinched to a point or a hole touches its outside. That is one ring in the graph but it
    // crosses itself as geometry, which NetTopologySuite rejects, so it is cut at every such
    // point into the separate lobes it is really made of.
    //
    // Which rings are ground and which are holes in it comes from the walk: a ring of positive
    // signed area encloses ground, a negative one a hole. Unlooping can turn one ring into
    // several, so the sign is taken from each lobe rather than from the ring it came out of.
    private IEnumerable<Polygon> CreatePolygons(IReadOnlyList<DirectedArc[]> rings)
    {
        List<LinearRing> shells = [];
        List<LinearRing> holes = [];
        foreach (DirectedArc[] ring in rings)
        {
            foreach (LinearRing lobe in this.Unloop(ring.GetCoordinates()))
            {
                if (GetSignedArea(lobe) > 0.0)
                {
                    shells.Add(lobe);
                }
                else
                {
                    holes.Add(lobe);
                }
            }
        }

        if (shells.Count == 0)
        {
            return [];
        }

        // With one shell every hole belongs to it. With several, each hole goes to the smallest
        // shell that encloses it.
        if (shells.Count == 1)
        {
            return [this.geometryFactory.CreatePolygon(shells[0], [.. holes])];
        }

        Polygon[] lobes = [.. shells.Select(shell => this.geometryFactory.CreatePolygon(shell))];
        List<LinearRing>[] byLobe = [.. lobes.Select(_ => new List<LinearRing>())];
        foreach (LinearRing hole in holes)
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
                byLobe[best].Add(hole);
            }
        }

        return shells.Select((shell, i) => this.geometryFactory.CreatePolygon(shell, [.. byLobe[i]]));
    }

    // Splits a closed path wherever it returns to a point it has already visited, which is
    // the same unlooping the drawn shapes get.
    private List<LinearRing> Unloop(List<Coordinate> path)
    {
        List<LinearRing> rings = [];
        List<Coordinate> current = [];
        Dictionary<Coordinate, int> seen = [];
        foreach (Coordinate coordinate in path)
        {
            if (seen.TryGetValue(coordinate, out int start))
            {
                LinearRing? ring = this.CreateRing(current.GetRange(start, current.Count - start));
                if (ring != null)
                {
                    rings.Add(ring);
                }

                for (int i = start; i < current.Count; ++i)
                {
                    seen.Remove(current[i]);
                }

                current.RemoveRange(start, current.Count - start);
            }

            seen[coordinate] = current.Count;
            current.Add(coordinate);
        }

        LinearRing? remainder = this.CreateRing(current);
        if (remainder != null)
        {
            rings.Add(remainder);
        }

        return rings;
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
