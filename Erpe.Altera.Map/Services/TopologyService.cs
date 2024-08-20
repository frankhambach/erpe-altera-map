// <copyright file="TopologyService.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;

public class TopologyService : ITopologyService
{
    private readonly GeometryFactory geometryFactory;

    public TopologyService(GeometryFactory geometryFactory)
    {
        this.geometryFactory = geometryFactory;
    }

    public LinearRing Close(LineString lineString)
    {
        return this.geometryFactory.CreateLinearRing(
            lineString.IsClosed
                ? lineString.Coordinates
                : lineString.Coordinates.Append(lineString.Coordinates[0].Copy()).ToArray());
    }

    public IEnumerable<Polygon> Polygonize(IReadOnlyCollection<LinearRing> linearRings)
    {
        Dictionary<LinearRing, LinearRing?> smallestEnclosingLinearRings = new Dictionary<LinearRing, LinearRing?>();

        FindSmallestEnclosingLinearRings(linearRings, smallestEnclosingLinearRings);
        return smallestEnclosingLinearRings.Where(keyValuePair => keyValuePair.Value is null)
            .SelectMany(keyValuePair => CreatePolygons(keyValuePair.Key, smallestEnclosingLinearRings));
    }

    public LinearRing Unloop(LinearRing linearRing)
    {
        Polygonizer polygonizer = new Polygonizer();
        LineString lineString = linearRing.Factory.CreateLineString(linearRing.CoordinateSequence);
        Point point = lineString.Factory.CreatePoint(lineString.GetCoordinateN(0));
        polygonizer.Add(lineString.Union(point));
        Polygon result = polygonizer.GetPolygons().OfType<Polygon>().MaxBy(polygon => polygon.Area) ?? Polygon.Empty;
        return result.Shell;
    }

    private static IEnumerable<Polygon> CreatePolygons(
        LinearRing enclosingLinearRing,
        IDictionary<LinearRing, LinearRing?> smallestEnclosingLinearRings)
    {
        LinearRing[] enclosedLinearRings = smallestEnclosingLinearRings
            .Where(keyValuePair => keyValuePair.Value == enclosingLinearRing)
            .Select(keyValuePair => keyValuePair.Key)
            .ToArray();
        return new[] { new Polygon(enclosingLinearRing, enclosedLinearRings) }.Concat(
            smallestEnclosingLinearRings
                .Where(
                    keyValuePair => Array.Exists(
                        enclosedLinearRings,
                        enclosedLinearRing => keyValuePair.Value == enclosedLinearRing))
                .SelectMany(keyValuePair => CreatePolygons(keyValuePair.Key, smallestEnclosingLinearRings)));
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
}