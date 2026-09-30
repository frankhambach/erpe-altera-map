// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Simplify;

public sealed class TopologyService : ITopologyService
{
    private readonly GeometryFactory geometryFactory;

    public TopologyService(GeometryFactory geometryFactory)
    {
        this.geometryFactory = geometryFactory;
    }

    public LinearRing Close(CoordinateSequence coordinateSequence)
    {
        ArgumentNullException.ThrowIfNull(coordinateSequence);

        return this.geometryFactory.CreateLinearRing(
            coordinateSequence.GetCoordinate(0).Equals2D(coordinateSequence.GetCoordinate(coordinateSequence.Count - 1))
                ? coordinateSequence.ToCoordinateArray()
                : coordinateSequence.ToCoordinateArray().Append(coordinateSequence.GetCoordinateCopy(0)).ToArray());
    }

    public IEnumerable<Polygon> Polygonize(IReadOnlyCollection<LinearRing> linearRings)
    {
        Dictionary<LinearRing, LinearRing?> smallestEnclosingLinearRings = new Dictionary<LinearRing, LinearRing?>();

        FindSmallestEnclosingLinearRings(linearRings, smallestEnclosingLinearRings);
        return smallestEnclosingLinearRings.Where(keyValuePair => keyValuePair.Value is null)
            .SelectMany(keyValuePair => this.CreatePolygons(keyValuePair.Key, smallestEnclosingLinearRings));
    }

    public LinearRing Unloop(LinearRing linearRing)
    {
        ArgumentNullException.ThrowIfNull(linearRing);

        Polygonizer polygonizer = new Polygonizer();
        LineString lineString = this.geometryFactory.CreateLineString(linearRing.CoordinateSequence);
        Point point = this.geometryFactory.CreatePoint(lineString.GetCoordinateN(0));
        polygonizer.Add(lineString.Union(point));
        Polygon result = polygonizer.GetPolygons().OfType<Polygon>().MaxBy(polygon => polygon.Area) ?? Polygon.Empty;
        return result.Shell;
    }

    private static void FindSmallestEnclosingLinearRings(
        IReadOnlyCollection<LinearRing> linearRings,
        IDictionary<LinearRing, LinearRing?> smallestEnclosingLinearRings)
    {
        foreach (LinearRing linearRing in linearRings.Where(
                     linearRing => !smallestEnclosingLinearRings.ContainsKey(linearRing)))
        {
            LinearRing[] enclosingLinearRings = linearRings.Where(
                    enclosingLinearRing => (enclosingLinearRing != linearRing)
                        && new Polygon(enclosingLinearRing).Contains(new Polygon(linearRing)))
                .ToArray();
            switch (enclosingLinearRings.Length)
            {
                case 0:
                    smallestEnclosingLinearRings[linearRing] = null;
                    break;
                case 1:
                    smallestEnclosingLinearRings[linearRing] = enclosingLinearRings[0];
                    break;
                default:
                    FindSmallestEnclosingLinearRings(enclosingLinearRings, smallestEnclosingLinearRings);
                    smallestEnclosingLinearRings[linearRing] = enclosingLinearRings.Single(
                        enclosingLinearRing => !Array.Exists(
                            enclosingLinearRings,
                            otherEnclosingLinearRing => smallestEnclosingLinearRings.Contains(
                                new KeyValuePair<LinearRing, LinearRing?>(
                                    otherEnclosingLinearRing,
                                    enclosingLinearRing))));
                    break;
            }
        }
    }

    private IEnumerable<Polygon> CreatePolygons(
        LinearRing enclosingLinearRing,
        IDictionary<LinearRing, LinearRing?> smallestEnclosingLinearRings)
    {
        LinearRing[] enclosedLinearRings = smallestEnclosingLinearRings
            .Where(keyValuePair => keyValuePair.Value == enclosingLinearRing)
            .Select(keyValuePair => keyValuePair.Key)
            .ToArray();
        return new[] { this.geometryFactory.CreatePolygon(enclosingLinearRing, enclosedLinearRings) }.Concat(
            smallestEnclosingLinearRings
                .Where(
                    keyValuePair => Array.Exists(
                        enclosedLinearRings,
                        enclosedLinearRing => keyValuePair.Value == enclosedLinearRing))
                .SelectMany(keyValuePair => this.CreatePolygons(keyValuePair.Key, smallestEnclosingLinearRings)));
    }
}