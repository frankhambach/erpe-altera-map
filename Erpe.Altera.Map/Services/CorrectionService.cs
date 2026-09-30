// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.OverlayNG;

public sealed class CorrectionService : ICorrectionService
{
    private readonly GeometryFactory geometryFactory;

    public CorrectionService(GeometryFactory geometryFactory)
    {
        this.geometryFactory = geometryFactory;
    }

    public IEnumerable<Gap> FindGaps(IList<Country> countries)
    {
        IEnumerable<Polygon> world = ((GeometryCollection)UnaryUnionNG.Union(
            countries.SelectMany(country => country.Shapes).Select(shape => shape.Polygon).ToArray(),
            this.geometryFactory,
            this.geometryFactory.PrecisionModel)).OfType<Polygon>();

        IEnumerable<Polygon> gaps = FindHoles(world).Concat(FindOverlaps(countries));

        Geometry combinedGaps = UnaryUnionNG.Union(gaps, this.geometryFactory, this.geometryFactory.PrecisionModel);

        return ((GeometryCollection)combinedGaps).OfType<Polygon>()
            .Select(
                combinedIntersection => new Gap(
                    combinedIntersection.Shell,
                    [..FindCoasts(combinedIntersection.Shell, countries.SelectMany(country => country.Shapes))]));
    }

    public void Snap(IEnumerable<Coordinate> coordinates, double snapTolerance)
    {
        ArgumentNullException.ThrowIfNull(coordinates);

        Coordinate[] orderedCoordinates = coordinates
            .OrderBy(coordinate => coordinate.X)
            .ToArray();

        for (int index = 0; index < orderedCoordinates.Length; ++index)
        {
            int nextIndex = index + 1;
            while (nextIndex < orderedCoordinates.Length)
            {
                double deltaX = orderedCoordinates[nextIndex].X - orderedCoordinates[index].X;
                if (deltaX > snapTolerance)
                {
                    break;
                }

                double deltaY = orderedCoordinates[nextIndex].Y - orderedCoordinates[index].Y;
                if (((deltaX * deltaX) + (deltaY * deltaY)) <= (snapTolerance * snapTolerance))
                {
                    orderedCoordinates[nextIndex].X = orderedCoordinates[index].X;
                    orderedCoordinates[nextIndex].Y = orderedCoordinates[index].Y;
                    break;
                }

                ++nextIndex;
            }
        }
    }

    private static IEnumerable<Polygon> ExtractPolygons(Geometry geometry)
    {
        return geometry switch
            {
                GeometryCollection geometryCollection => geometryCollection.OfType<Polygon>(),
                Polygon polygon => [polygon], _ => [],
            };
    }

    private static IEnumerable<Coast> FindCoasts(LinearRing boundary, IEnumerable<Shape> shapes)
    {
        foreach (Shape shape in shapes.Distinct())
        {
            Geometry intersection = shape.Polygon.Boundary.Intersection(boundary);
            if (!intersection.IsEmpty)
            {
                foreach (LineString lineString in intersection switch
                             {
                                 GeometryCollection geometryCollection => MergeLineStrings(
                                     geometryCollection.OfType<LineString>()),
                                 LineString lineString => [lineString], _ => [],
                             })
                {
                    yield return new Coast(lineString, shape);
                }
            }
        }
    }

    private static IEnumerable<Polygon> FindHoles(IEnumerable<Polygon> world)
    {
        return world.SelectMany(polygon => polygon.Holes)
            .Select(linearRing => new Polygon(linearRing))
            .Where(polygon => (polygon.Area < 0.007) || ((polygon.Area / polygon.Length) < 0.01));
    }

    private static IEnumerable<Polygon> FindOverlaps(IList<Country> countries)
    {
        List<(Polygon Overlap, Shape[] Shapes)> overlaps = countries.SelectMany(country => country.Shapes)
            .SelectMany(
                (firstShape, firstIndex) => countries.SelectMany(country => country.Shapes)
                    .Where((_, secondIndex) => secondIndex > firstIndex)
                    .Select(
                        secondShape => (Intersection: firstShape.Polygon.Intersection(secondShape.Polygon),
                            Shapes: new[] { firstShape, secondShape })))
            .Where(
                intersection => !intersection.Intersection.IsEmpty
                    && (intersection.Intersection.Dimension
                        == Dimension.Surface)) // check if intersection is tiny 1E-25
            .SelectMany(
                intersection => ExtractPolygons(intersection.Intersection)
                    .Select<Polygon, (Polygon Overlap, Shape[] Shapes)>(
                        polygon => (Overlap: polygon, intersection.Shapes)))
            .ToList();

        List<Polygon> exclaves = new List<Polygon>();
        foreach ((Shape shape, Polygon[] remainingPolygons) in overlaps
                     .SelectMany(overlap => overlap.Shapes.Select(shape => (Shape: shape, overlap.Overlap)))
                     .GroupBy(shape => shape.Shape)
                     .Select(
                         grouping => (Shape: grouping.Key,
                             RemainingPolygons: grouping.Aggregate(
                                 new[] { grouping.Key.Polygon },
                                 (remainingPolygons, shape) =>
                                     ExtractPolygons(new MultiPolygon(remainingPolygons).Difference(shape.Overlap))
                                         .ToArray())))
                     .ToArray())
        {
            shape.Polygon = remainingPolygons.MaxBy(polygon => polygon.Area) ?? Polygon.Empty;
            exclaves.AddRange(remainingPolygons.Except([shape.Polygon]));
        }

        return overlaps.Select(overlap => overlap.Overlap).Concat(exclaves);
    }

    private static IEnumerable<LineString> MergeLineStrings(IEnumerable<LineString> lineStrings)
    {
        LineMerger lineMerger = new LineMerger();
        lineMerger.Add(lineStrings);
        return lineMerger.GetMergedLineStrings().OfType<LineString>();
    }
}