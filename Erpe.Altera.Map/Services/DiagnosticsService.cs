// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

// Everything here writes SVG in the coordinates of the source map, untouched by the
// projection, so that any stage can be opened on top of the file it was read from and
// compared against it directly. Every number is turned into a string before it reaches an
// interpolation, so that nothing can pick up the current culture on the way out.
public sealed class DiagnosticsService : IDiagnosticsService
{
    private const string BackgroundColour = "#ffffff";

    private const string BorderColour = "#d81b60";

    private const string CoastColour = "#1565c0";

    // Four decimals. The noding grid is 0.0001 units, so that is the finest two
    // coordinates in the graph can genuinely differ by, and anything beyond it would be
    // writing out rounding.
    private const string CoordinateFormat = "0.####";

    private const string FaceStrokeColour = "#37474f";

    // The golden angle, which spaces successive hues as far apart as they will go, so that
    // faces next to each other come out in different colours however the identifiers run.
    private const double GoldenAngle = 137.508;

    private const string LakeColour = "#00acc1";

    private const string NodeColour = "#000000";

    private const double NodeRadiusFactor = 1.5;

    private const double PaddingFactor = 10.0;

    // Line width as a fraction of the longer side rather than a fixed number of units, so
    // that a file covering the whole map and one covering a handful of faces are both
    // legible.
    private const double StrokeDivisor = 4000.0;

    private const string SubstateColour = "#f9a825";

    private const string UnownedFaceColour = "#cfd8dc";

    public Task WriteArcsAsync(IEnumerable<Arc> arcs, FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(arcs);
        ArgumentNullException.ThrowIfNull(file);

        return File.WriteAllLinesAsync(file.FullName, GetArcLines([.. arcs], file));
    }

    public Task WriteFacesAsync(IEnumerable<Face> faces, FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(file);

        return File.WriteAllLinesAsync(file.FullName, GetFaceLines([.. faces], file));
    }

    private static string Escape(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
    }

    private static string Format(double value)
    {
        return value.ToString(CoordinateFormat, CultureInfo.InvariantCulture);
    }

    private static string Format(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatArc(Arc arc, string stroke)
    {
        string id = Format(arc.Id);
        string points = string.Join(
            " ",
            arc.GetFullCoordinates().Select(coordinate => Format(coordinate.X) + "," + Format(coordinate.Y)));

        return $"<polyline id=\"arc-{id}\" data-arc-id=\"{id}\" data-kind=\"{arc.Kind}\" "
            + $"stroke=\"{GetStroke(arc.Kind)}\" stroke-width=\"{stroke}\" points=\"{points}\" />";
    }

    private static string FormatFace(Face face)
    {
        string data = string.Join(
            " ",
            face.Holes.Prepend(face.Shell).Select(FormatSubpath).Where(subpath => subpath.Length > 0));
        if (data.Length == 0)
        {
            return string.Empty;
        }

        string id = Format(face.Id);
        string owner = face.Owner == null ? string.Empty : $" data-owner=\"{Escape(face.Owner.Id)}\"";

        return $"<path id=\"face-{id}\" data-face-id=\"{id}\"{owner} fill=\"{GetFill(face)}\" d=\"{data}\" />";
    }

    private static string FormatNode(Node node, string radius)
    {
        string id = Format(node.Id);

        return $"<circle id=\"node-{id}\" data-node-id=\"{id}\" cx=\"{Format(node.Coordinate.X)}\" "
            + $"cy=\"{Format(node.Coordinate.Y)}\" r=\"{radius}\" />";
    }

    // One subpath per ring, with the coordinate pairs after the first taken as implicit
    // line segments. That is the compact form of the same path, and these files run to
    // tens of megabytes.
    private static string FormatSubpath(ImmutableArray<DirectedArc> ring)
    {
        List<Coordinate> coordinates = ring.GetCoordinates();
        if (coordinates.Count < 3)
        {
            return string.Empty;
        }

        return "M "
            + string.Join(" ", coordinates.Select(coordinate => Format(coordinate.X) + " " + Format(coordinate.Y)))
            + " Z";
    }

    private static IEnumerable<string> GetArcLines(Arc[] arcs, FileInfo file)
    {
        Envelope envelope = GetEnvelope(arcs.SelectMany(arc => arc.GetFullCoordinates()));
        double stroke = GetStrokeWidth(envelope);

        return
        [
            .. GetHeaderLines(file, envelope),
            "<g id=\"arcs\" fill=\"none\" stroke-linecap=\"round\">",
            .. arcs.Select(arc => FormatArc(arc, Format(stroke))),
            "</g>",

            // The nodes are where welding decided two drawn ends were meant to be the same
            // point, so they are what makes a welded graph worth looking at rather than
            // just a pile of lines.
            $"<g id=\"nodes\" stroke=\"none\" fill=\"{NodeColour}\">",
            .. arcs.SelectMany(arc => new[] { arc.StartNode, arc.EndNode })
                .Distinct()
                .Select(node => FormatNode(node, Format(stroke * NodeRadiusFactor))),
            "</g>",
            "</svg>",
        ];
    }

    private static Envelope GetEnvelope(IEnumerable<Coordinate> coordinates)
    {
        Envelope envelope = new Envelope();
        foreach (Coordinate coordinate in coordinates)
        {
            envelope.ExpandToInclude(coordinate);
        }

        return envelope.IsNull ? new Envelope(0.0, 1.0, 0.0, 1.0) : envelope;
    }

    private static IEnumerable<string> GetFaceLines(Face[] faces, FileInfo file)
    {
        Envelope envelope = GetEnvelope(
            faces.SelectMany(face => face.Holes.Prepend(face.Shell))
                .SelectMany(ring => ring)
                .SelectMany(directed => directed.Arc.GetFullCoordinates()));
        string stroke = Format(GetStrokeWidth(envelope));

        return
        [
            .. GetHeaderLines(file, envelope),

            // Even-odd, because the holes are written as further subpaths of the same path
            // and the traced rings carry no reliable winding of their own.
            $"<g id=\"faces\" fill-rule=\"evenodd\" stroke=\"{FaceStrokeColour}\" stroke-width=\"{stroke}\">",
            .. faces.Select(FormatFace).Where(line => line.Length > 0),
            "</g>",
            "</svg>",
        ];
    }

    private static string GetFill(Face face)
    {
        // Grey for a face no country claims: the ocean, the lakes, and whatever the border
        // network enclosed that nothing covers.
        return face.Owner == null
            ? UnownedFaceColour
            : $"hsl({Format((int)((face.Id * GoldenAngle) % 360.0))}, 55%, 72%)";
    }

    private static IEnumerable<string> GetHeaderLines(FileInfo file, Envelope envelope)
    {
        Envelope padded = envelope.Copy();
        padded.ExpandBy(GetStrokeWidth(envelope) * PaddingFactor);
        string x = Format(padded.MinX);
        string y = Format(padded.MinY);
        string width = Format(padded.Width);
        string height = Format(padded.Height);

        return
        [
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{x} {y} {width} {height}\">",
            $"<title>{Escape(Path.GetFileNameWithoutExtension(file.Name))}</title>",
            $"<rect id=\"background\" x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\" "
            + $"fill=\"{BackgroundColour}\" />",
        ];
    }

    private static string GetStroke(ArcKind kind)
    {
        return kind switch
            {
                ArcKind.Border => BorderColour,
                ArcKind.Substate => SubstateColour,
                ArcKind.Coast => CoastColour,
                ArcKind.Lake => LakeColour,
                _ => NodeColour,
            };
    }

    private static double GetStrokeWidth(Envelope envelope)
    {
        return Math.Max(envelope.Width, envelope.Height) / StrokeDivisor;
    }
}
