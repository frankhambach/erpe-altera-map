// <copyright file="TopologyExtensions.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map;

using System;
using System.Collections.Generic;
using System.Linq;

using NetTopologySuite.Geometries;

public static class TopologyExtensions
{
    public static IEnumerable<Polygon> ToPolygons(this IReadOnlyCollection<LinearRing> linearRings)
    {
        Dictionary<LinearRing, LinearRing?> exteriorLinearRings = new Dictionary<LinearRing, LinearRing?>();

        FindOuterLinearRings(linearRings, exteriorLinearRings);
        return exteriorLinearRings.Where(keyValuePair => keyValuePair.Value is null)
            .SelectMany(keyValuePair => CreatePolygons(keyValuePair.Key, exteriorLinearRings));
    }

    private static IEnumerable<Polygon> CreatePolygons(LinearRing exteriorRing, IDictionary<LinearRing, LinearRing?> exteriorLinearRings)
    {
        LinearRing[] interiorRings = exteriorLinearRings.Where(keyValuePair => keyValuePair.Value == exteriorRing)
            .Select(keyValuePair => keyValuePair.Key).ToArray();
        return new[] { new Polygon(exteriorRing, interiorRings) }.Concat(
            exteriorLinearRings
                .Where(keyValuePair => Array.Exists(interiorRings, interiorRing => keyValuePair.Value == interiorRing))
                .SelectMany(keyValuePair => CreatePolygons(keyValuePair.Key, exteriorLinearRings)));
    }

    private static void FindOuterLinearRings(
        IReadOnlyCollection<LinearRing> linearRings,
        IDictionary<LinearRing, LinearRing?> exteriorLinearRings)
    {
        foreach (LinearRing linearRing in linearRings.Where(linearRing => !exteriorLinearRings.ContainsKey(linearRing)))
        {
            LinearRing[] outerLinearRings =
                linearRings.Where(outerLinearRing => outerLinearRing != linearRing && new Polygon(outerLinearRing).Contains(new Polygon(linearRing))).ToArray();
            switch (outerLinearRings.Length)
            {
                case 0:
                    exteriorLinearRings[linearRing] = null;
                    break;
                case 1:
                    exteriorLinearRings[linearRing] = outerLinearRings[0];
                    break;
                default:
                    FindOuterLinearRings(outerLinearRings, exteriorLinearRings);
                    exteriorLinearRings[linearRing] = outerLinearRings.Single(
                        outerLinearRing => !Array.Exists(
                            outerLinearRings,
                            otherOuterLinearRing => exteriorLinearRings.Contains(
                                new KeyValuePair<LinearRing, LinearRing?>(otherOuterLinearRing, outerLinearRing))));
                    break;
            }
        }
    }
}