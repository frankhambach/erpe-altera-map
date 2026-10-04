// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.OverlayNG;

using Serilog;

public sealed class AssemblyService : IAssemblyService
{
    private readonly GeometryFactory geometryFactory;

    public AssemblyService(GeometryFactory geometryFactory)
    {
        ArgumentNullException.ThrowIfNull(geometryFactory);

        this.geometryFactory = geometryFactory;
    }

    // Replaces each country's drawn shapes with the faces it was assigned. The faces are
    // what the border network actually encloses, so from here on a border exists once and
    // both countries either side of it are built from the very same arc coordinates.
    public void Assemble(ArcGraph arcGraph, IEnumerable<Country> countries)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(countries);

        Dictionary<Country, List<Polygon>> byOwner = [];
        int invalid = 0;
        double invalidArea = 0.0;
        foreach (Face face in arcGraph.Faces.Where(face => face.Owner != null))
        {
            foreach (Polygon polygon in this.CreatePolygons(face))
            {
                if (polygon.IsEmpty)
                {
                    continue;
                }

                if (!polygon.IsValid)
                {
                    ++invalid;
                    invalidArea += Math.Abs(polygon.Area);
                    continue;
                }

                if (!byOwner.TryGetValue(face.Owner!, out List<Polygon>? polygons))
                {
                    polygons = [];
                    byOwner.Add(face.Owner!, polygons);
                }

                polygons.Add(polygon);
            }
        }

        int assembled = 0;
        foreach (Country country in countries)
        {
            country.Shapes.Clear();
            if (!byOwner.TryGetValue(country, out List<Polygon>? polygons))
            {
                continue;
            }

            // A country holds several faces wherever a substate border cuts through it, so
            // the faces have to be dissolved back together. Unioning geometry that already
            // shares its edges exactly follows those edges rather than recomputing them, so
            // the borders with the neighbours come through untouched.
            foreach ((Polygon polygon, int index) in ExtractPolygons(
                             UnaryUnionNG.Union(
                                 polygons.ToArray(),
                                 this.geometryFactory,
                                 this.geometryFactory.PrecisionModel))
                         .Select((polygon, index) => (polygon, index)))
            {
                country.Shapes.Add(new Shape($"{country.Id}-{index}", polygon));
                ++assembled;
            }
        }

        Log.Information(
            "Assembled {Shapes} shapes for {Countries} countries from the faces.",
            assembled,
            byOwner.Count);

        if (invalid > 0)
        {
            Log.Warning(
                "{Invalid} faces trace to a ring that crosses itself, covering {Area:0.0} square units, and were "
                + "left out.",
                invalid,
                invalidArea);
        }
    }

    private static IEnumerable<Polygon> ExtractPolygons(Geometry geometry)
    {
        return geometry switch
            {
                GeometryCollection geometryCollection => geometryCollection.OfType<Polygon>(),
                Polygon polygon => [polygon],
                _ => [],
            };
    }

    // The ring as a run of coordinates, with the closing repeat left off so that unlooping
    // sees a genuine revisit rather than the ring simply ending where it began.
    private static List<Coordinate> GetCoordinates(ImmutableArray<DirectedArc> ring)
    {
        List<Coordinate> coordinates = [];
        foreach (DirectedArc directed in ring)
        {
            Coordinate[] arcCoordinates = directed.Arc.GetFullCoordinates();
            if (directed.IsReversed)
            {
                Array.Reverse(arcCoordinates);
            }

            foreach (Coordinate coordinate in arcCoordinates)
            {
                if ((coordinates.Count == 0) || !coordinates[^1].Equals2D(coordinate))
                {
                    coordinates.Add(coordinate);
                }
            }
        }

        if ((coordinates.Count > 1) && coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates.RemoveAt(coordinates.Count - 1);
        }

        return coordinates;
    }

    // A face boundary may come back to a node it has already been through, where the face is
    // pinched to a point or a hole touches its shell. That is one ring in the graph but it
    // crosses itself as geometry, which NetTopologySuite rejects, so it is cut at every such
    // point into the separate lobes it is really made of.
    private IEnumerable<Polygon> CreatePolygons(Face face)
    {
        LinearRing[] shells = [.. this.Unloop(GetCoordinates(face.Shell))];
        if (shells.Length == 0)
        {
            return [];
        }

        LinearRing[] holes = [.. face.Holes.SelectMany(hole => this.Unloop(GetCoordinates(hole)))];

        // With one lobe every hole belongs to it. With several, each hole goes to the lobe
        // that encloses it.
        if (shells.Length == 1)
        {
            return [this.geometryFactory.CreatePolygon(shells[0], holes)];
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
