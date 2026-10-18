// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

using Serilog;

// TopoJSON falls out of this pipeline rather than having to be recovered from it, which is what
// the arc graph was built for. An arc exists once, the two countries either side of it refer to
// the same one by index, and nothing has to be deduced by comparing coordinates.
public sealed class TopoJsonService : ITopoJsonService
{
    // The quantization the file is written at, as the reciprocal of the scale in its transform.
    // One millionth of a degree, which is the six decimals the GeoJson is written to, so the two
    // files carry the same numbers and a consumer that turns one into the other gets the other.
    private const double QuantizationScale = 1000000.0;

    private const int OutputDecimals = 6;

    public async Task WriteAsync(
        IReadOnlyList<(string Name, IReadOnlyList<Country> Countries)> layers,
        IReadOnlyDictionary<Arc, Coordinate[]> arcs,
        FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(arcs);
        ArgumentNullException.ThrowIfNull(file);

        // Only the arcs something refers to, numbered by their own identifier so that the file
        // comes out the same way every run. A lake nobody borders carries no reference and would
        // otherwise be written for nothing.
        Arc[] referenced =
        [
            .. layers.SelectMany(layer => layer.Countries)
                .SelectMany(country => country.Shapes)
                .SelectMany(shape => shape.Rings)
                .SelectMany(ring => ring)
                .Select(directed => directed.Arc)
                .Distinct()
                .Where(arcs.ContainsKey)
                .OrderBy(arc => arc.Id),
        ];
        Dictionary<Arc, int> indexes = referenced.Select((arc, index) => (arc, index))
            .ToDictionary(pair => pair.arc, pair => pair.index);

        FileStream stream = file.Create();
        await using (stream.ConfigureAwait(false))
        {
            Utf8JsonWriter writer = new Utf8JsonWriter(stream);
            await using (writer.ConfigureAwait(false))
            {
                WriteTopology(writer, layers, arcs, referenced, indexes);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        Log.Information(
            "Wrote {Layers} layers over {Arcs} shared arcs to {File}.",
            layers.Count,
            referenced.Length,
            file.Name);
    }

    private static void WriteTopology(
        Utf8JsonWriter writer,
        IReadOnlyList<(string Name, IReadOnlyList<Country> Countries)> layers,
        IReadOnlyDictionary<Arc, Coordinate[]> arcs,
        Arc[] referenced,
        Dictionary<Arc, int> indexes)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Topology");

        writer.WriteStartObject("transform");
        WriteNumbers(writer, "scale", [1.0 / QuantizationScale, 1.0 / QuantizationScale]);
        WriteNumbers(writer, "translate", [0.0, 0.0]);
        writer.WriteEndObject();

        writer.WriteStartObject("objects");
        foreach ((string name, IReadOnlyList<Country> countries) in layers)
        {
            WriteLayer(writer, name, countries, arcs, indexes);
        }

        writer.WriteEndObject();

        WriteArcs(writer, referenced, arcs);
        writer.WriteEndObject();
    }

    private static long Quantize(double value)
    {
        // Through the output decimals first, so that what this writes decodes to exactly what the
        // GeoJson writer rounds to rather than to a neighbour of it.
        return (long)Math.Round(Math.Round(value, OutputDecimals) * QuantizationScale);
    }

    private static Coordinate[] GetCoordinates(
        IReadOnlyList<DirectedArc> ring,
        IReadOnlyDictionary<Arc, Coordinate[]> arcs)
    {
        List<Coordinate> coordinates = [];
        foreach (DirectedArc directed in ring)
        {
            if (!arcs.TryGetValue(directed.Arc, out Coordinate[]? line))
            {
                continue;
            }

            IEnumerable<Coordinate> run = directed.IsReversed ? line.Reverse() : line;
            foreach (Coordinate coordinate in run)
            {
                if ((coordinates.Count == 0) || !coordinates[^1].Equals2D(coordinate))
                {
                    coordinates.Add(coordinate);
                }
            }
        }

        return [.. coordinates];
    }

    // The specification asks nothing about which way a ring is wound, so this follows the GeoJson
    // beside it: RFC 7946's counter-clockwise shells and clockwise holes. A consumer turning the
    // topology back into features then gets what the other file already says.
    private static IReadOnlyList<DirectedArc> Orient(
        IReadOnlyList<DirectedArc> ring,
        IReadOnlyDictionary<Arc, Coordinate[]> arcs,
        bool isHole)
    {
        // Over the ring as it will be written, not as the dissolve sorted it: that was done in the
        // source map's coordinates, where y runs down the page. A ring enclosing nothing is left
        // as it is, so that this and the GeoJson writer agree about it.
        int winding = GetCoordinates(ring, arcs).GetWinding();
        if ((winding == 0) || ((winding > 0) != isHole))
        {
            return ring;
        }

        return [.. ring.Reverse().Select(directed => new DirectedArc(directed.Arc, !directed.IsReversed))];
    }

    private static void WriteNumbers(Utf8JsonWriter writer, string name, double[] values)
    {
        writer.WriteStartArray(name);
        foreach (double value in values)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteRing(
        Utf8JsonWriter writer,
        IReadOnlyList<DirectedArc> ring,
        IReadOnlyDictionary<Arc, Coordinate[]> arcs,
        Dictionary<Arc, int> indexes,
        bool isHole)
    {
        writer.WriteStartArray();
        foreach (DirectedArc directed in Orient(ring, arcs, isHole))
        {
            if (!indexes.TryGetValue(directed.Arc, out int index))
            {
                continue;
            }

            // A negative index means the arc at its ones' complement, walked backwards. So the
            // first arc reversed is -1, the second -2, and there is no negative zero to trip over.
            writer.WriteNumberValue(directed.IsReversed ? ~index : index);
        }

        writer.WriteEndArray();
    }

    private static void WriteShape(
        Utf8JsonWriter writer,
        Shape shape,
        IReadOnlyDictionary<Arc, Coordinate[]> arcs,
        Dictionary<Arc, int> indexes)
    {
        writer.WriteStartArray();
        for (int i = 0; i < shape.Rings.Count; ++i)
        {
            WriteRing(writer, shape.Rings[i], arcs, indexes, i > 0);
        }

        writer.WriteEndArray();
    }

    private static void WriteLayer(
        Utf8JsonWriter writer,
        string name,
        IReadOnlyList<Country> countries,
        IReadOnlyDictionary<Arc, Coordinate[]> arcs,
        Dictionary<Arc, int> indexes)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "GeometryCollection");
        writer.WriteStartArray("geometries");
        foreach (Country country in countries.Where(country => country.Shapes.Count > 0))
        {
            writer.WriteStartObject();
            if (country.Shapes.Count == 1)
            {
                writer.WriteString("type", "Polygon");
                writer.WritePropertyName("arcs");
                WriteShape(writer, country.Shapes[0], arcs, indexes);
            }
            else
            {
                writer.WriteString("type", "MultiPolygon");
                writer.WriteStartArray("arcs");
                foreach (Shape shape in country.Shapes)
                {
                    WriteShape(writer, shape, arcs, indexes);
                }

                writer.WriteEndArray();
            }

            writer.WriteString("id", country.Id);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // Quantized and delta-encoded, as the specification requires of a topology with a transform:
    // the first position of an arc stands on its own and every one after it is the step from the
    // one before.
    private static void WriteArcs(
        Utf8JsonWriter writer,
        Arc[] referenced,
        IReadOnlyDictionary<Arc, Coordinate[]> arcs)
    {
        writer.WriteStartArray("arcs");
        foreach (Arc arc in referenced)
        {
            Coordinate[] line = arcs[arc];
            writer.WriteStartArray();
            long previousX = 0;
            long previousY = 0;
            for (int i = 0; i < line.Length; ++i)
            {
                long x = Quantize(line[i].X);
                long y = Quantize(line[i].Y);
                writer.WriteStartArray();
                writer.WriteNumberValue(i == 0 ? x : x - previousX);
                writer.WriteNumberValue(i == 0 ? y : y - previousY);
                writer.WriteEndArray();
                previousX = x;
                previousY = y;
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
}
