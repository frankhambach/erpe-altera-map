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

    // The projection's own frame, drawn under everything else and in a grey that stays out of
    // the way: it is there to say where on the map you are, not to be read.
    private const string RimColour = "#78909c";

    private const double NodeRadiusFactor = 1.5;

    private const double PaddingFactor = 10.0;

    // Line width as a fraction of the longer side rather than a fixed number of units, so
    // that a file covering the whole map and one covering a handful of faces are both
    // legible.
    private const double StrokeDivisor = 4000.0;

    private const string SubstateColour = "#f9a825";

    private const string UnownedFaceColour = "#cfd8dc";

    public Task WriteArcsAsync(
        IEnumerable<Arc> arcs,
        IEnumerable<CoordinateSequence> rim,
        Envelope? view,
        FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(arcs);
        ArgumentNullException.ThrowIfNull(rim);
        ArgumentNullException.ThrowIfNull(file);

        return File.WriteAllLinesAsync(file.FullName, GetArcLines([.. arcs], [.. rim], view, file));
    }

    public Task WriteFacesAsync(IEnumerable<Face> faces, Envelope? view, FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(file);

        return File.WriteAllLinesAsync(file.FullName, GetFaceLines([.. faces], view, file));
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

    // The colour and the width sit on the group rather than on every line, which is what makes
    // the groups worth having: one file carries every kind and each can be turned off on its own.
    private static string FormatArc(Arc arc)
    {
        string id = Format(arc.Id);

        return $"<polyline id=\"arc-{id}\" data-arc-id=\"{id}\" data-kind=\"{arc.Kind}\" "
            + $"points=\"{FormatPoints(arc.GetFullCoordinates())}\" />";
    }

    private static string FormatPoints(IEnumerable<Coordinate> coordinates)
    {
        return string.Join(
            " ",
            coordinates.Select(coordinate => Format(coordinate.X) + "," + Format(coordinate.Y)));
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

    private static IEnumerable<string> GetArcLines(
        Arc[] arcs,
        CoordinateSequence[] rim,
        Envelope? view,
        FileInfo file)
    {
        // The source document's view box when there is one, used exactly as it stands: that is
        // what puts these files on the same coordinates as the map, whitespace and all. Only
        // without one is the extent fitted to the arcs and padded.
        Envelope envelope = view ?? Pad(GetEnvelope(arcs.SelectMany(arc => arc.GetFullCoordinates())));
        double stroke = GetStrokeWidth(envelope);

        return
        [
            .. GetHeaderLines(file, envelope),
            .. GetRimLines(rim, Format(stroke)),
            .. GetKindLines(arcs, Format(stroke)),

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

    // One group per kind, in the order they want to be read in: the coast and the lakes are the
    // ground, the tracers go over them, and the borders go over the substates.
    private static List<string> GetKindLines(Arc[] arcs, string stroke)
    {
        (ArcKind Kind, string Group)[] kinds =
        [
            (ArcKind.Coast, "coast"),
            (ArcKind.Lake, "lakes"),
            (ArcKind.Substate, "substates"),
            (ArcKind.Border, "borders"),
        ];

        List<string> lines = [];
        foreach ((ArcKind kind, string group) in kinds)
        {
            Arc[] ofKind = [.. arcs.Where(arc => arc.Kind == kind)];
            if (ofKind.Length == 0)
            {
                continue;
            }

            lines.Add(
                $"<g id=\"{group}\" fill=\"none\" stroke=\"{GetStroke(kind)}\" stroke-width=\"{stroke}\" "
                + "stroke-linecap=\"round\">");
            lines.AddRange(ofKind.Select(FormatArc));
            lines.Add("</g>");
        }

        return lines;
    }

    private static IEnumerable<string> GetRimLines(CoordinateSequence[] rim, string stroke)
    {
        if (rim.Length == 0)
        {
            return [];
        }

        return
        [
            $"<g id=\"rim\" fill=\"none\" stroke=\"{RimColour}\" stroke-width=\"{stroke}\">",
            .. rim.Select(sequence => $"<polyline points=\"{FormatPoints(sequence.ToCoordinateArray())}\" />"),
            "</g>",
        ];
    }

    private static Envelope Pad(Envelope envelope)
    {
        Envelope padded = envelope.Copy();
        padded.ExpandBy(GetStrokeWidth(envelope) * PaddingFactor);
        return padded;
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

    private static IEnumerable<string> GetFaceLines(Face[] faces, Envelope? view, FileInfo file)
    {
        // The source document's view box when there is one, so that every file a run writes is
        // framed the same way and they can be stacked on each other and on the input. Only
        // without one is the extent fitted to the faces and padded.
        //
        // This is a trade and not a free improvement. The dropped file holds a handful of faces
        // and often tiny ones - the two on this map are 0.2 units across - and fitted to its own
        // contents it showed them filling the frame. Framed on the document they are specks, so
        // what it is good for now is saying where they are rather than what they look like. The
        // faces file loses nothing, since it covers the map anyway. Both are consistent with the
        // arc files, which is what was wanted: four files that overlay.
        Envelope envelope = view
            ?? Pad(
                GetEnvelope(
                    faces.SelectMany(face => face.Holes.Prepend(face.Shell))
                        .SelectMany(ring => ring)
                        .SelectMany(directed => directed.Arc.GetFullCoordinates())));
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
        string x = Format(envelope.MinX);
        string y = Format(envelope.MinY);
        string width = Format(envelope.Width);
        string height = Format(envelope.Height);

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
