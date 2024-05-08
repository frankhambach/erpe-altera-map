// <copyright file="ConvertCommand.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;

using Serilog;

using Svg;

public class ConvertCommand
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters =
                {
                    new GeoJsonConverterFactory(),
                },
        };

    public async Task HandleAsync(FileInfo inFile, FileInfo outFile)
    {
        FeatureCollection featureCollection = new FeatureCollection();

        SvgDocument document = SvgDocument.Open<SvgDocument>(inFile.FullName, null);
        Envelope sourceEnvelope = GetEnvelope(document);

        SvgElement[] shapesElements = document.Descendants()
            .Where(element => element.ID is "Country_Shapes" or "Substate_Shapes")
            .ToArray();

        (Polygon Polygon, string Id)[] polygons = (await Task.WhenAll(
                shapesElements.SelectMany(shapesElement => shapesElement.Descendants())
                    .Where(element => element is SvgPath or SvgRectangle or SvgPolygon)
                    .Select(
                        element => (Element: element,
                            Id: element.ParentsAndSelf.TakeWhile(parent => !shapesElements.Contains(parent))
                                .First(
                                    parent => !string.IsNullOrEmpty(parent.ID)
                                        && !parent.ID.StartsWith("_x2A_")
                                        && !parent.ID.StartsWith("Islands_")
                                        && !parent.ID.StartsWith("Mainland_")
                                        && !parent.ID.StartsWith("_x3C_Path_x3E"))
                                ?.ID))
                    .Where(pathAndId => (pathAndId.Id != null) && !pathAndId.Id.EndsWith("_x2B_"))
                    .Select(
                        element =>
                        {
                            Log.Information("Processing {Id}", element.Id);
                            return element;
                        })
                    .Select(
                        async element => (
                            LinearRings: await element.Element.ToLinearRings().TransformAsync(sourceEnvelope),
                            Id: IdToName(element.Id!))))).Where(linearRings => linearRings.LinearRings.Length > 0)
            .SelectMany(
                linearRings => linearRings.LinearRings.ToPolygons()
                    .Select(polygon => (Polygon: polygon, linearRings.Id)))
            .ToArray();

        Dictionary<string, Geometry> geometries = polygons.GroupBy(polygonAndId => polygonAndId.Id)
            .ToDictionary<IGrouping<string, (Polygon Polygon, string Id)>, string, Geometry>(
                grouping => grouping.Key,
                grouping => grouping.Select(polygonAndId => polygonAndId.Polygon)
                    .Distinct(new ExactPolygonEqualityComparer())
                    .Select(polygon => polygon.RemoveSelfIntersections())
                    .Aggregate((firstGeometry, secondGeometry) => firstGeometry.Union(secondGeometry)));

        foreach (string id in geometries.Keys)
        {
            Feature feature = new Feature
                {
                    Attributes = new AttributesTable(),
                    Geometry = geometries[id],
                };
            feature.Attributes.Add("id", id);
            featureCollection.Add(feature);
        }

        string convertedOutFileName = Path.GetRandomFileName();
        try
        {
            File.WriteAllText(convertedOutFileName, JsonSerializer.Serialize(featureCollection, JsonSerializerOptions));

            Process? process = Process.Start(
                new ProcessStartInfo
                    {
                        FileName = "mapshaper",
                        Arguments =
                            $"\"{convertedOutFileName}\" -clean allow-empty rewind gap-fill-area=0 snap-interval=0.005 -o \"{outFile.FullName}\" format=geojson",
                        UseShellExecute = true,
                    });
            process?.WaitForExit();
        }
        finally
        {
            File.Delete(convertedOutFileName);
        }
    }

    private static Envelope GetEnvelope(SvgDocument document)
    {
        List<Point> rimPoints = document.Descendants()
            .First(element => element.ID == "Rim")
            .Descendants()
            .OfType<SvgPath>()
            .SelectMany(path => path.PathData.ToAbsolute().Select(pathSegment => pathSegment.End.ToPoint()))
            .ToList();
        return new Envelope(
            rimPoints.Min(point => point.X),
            rimPoints.Max(point => point.X),
            rimPoints.Min(point => point.Y),
            rimPoints.Max(point => point.Y));
    }

    private static string IdToName(string id)
    {
        string name = Regex.Replace(id, @"_\d+_$", string.Empty)
            .Replace("_x27_", "'")
            .Replace("_x28_", "(")
            .Replace("_x29_", ")")
            .Replace("_x2A_", "*")
            .Replace("_x2B_", "+")
            .Replace("_", " ");
        int index = name.IndexOf(" (", StringComparison.InvariantCulture);
        if (index > 0)
        {
            name = name[..index];
        }

        return name;
    }

    private sealed class ExactPolygonEqualityComparer : EqualityComparer<Polygon>
    {
        public override bool Equals(Polygon? x, Polygon? y)
        {
            return ((x == null) && (y == null)) || ((x != null) && (y != null) && x.EqualsExact(y));
        }

        public override int GetHashCode(Polygon obj)
        {
            return obj.Coordinates.Aggregate(
                23,
                (hashCode, coordinate) => HashCode.Combine(hashCode, coordinate.GetHashCode()));
        }
    }
}