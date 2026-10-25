// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;

using Serilog;

// The faces of the sea are traced the way the land's are, and given their owners the way the
// land's faces are told from water: by what bounds them, not by where they lie. Every face lies to
// the left of the arcs it is walked from, so each arc on its boundary says whose face it is. A
// shore says land if it is walked the way the land face walks it and its country if the other way,
// since the water beside a country's shore is nearer that shore than any other. A median line says
// which of its two countries is on that side, and a limit its country on the one side and the high
// seas on the other.
//
// The land graph is never changed by any of it. The shores and the rim over land go into the sea
// graph as the very arcs the land is built from, and nothing here nodes, merges or prunes: the zone
// boundaries already meet the shore only at its nodes, and the rim over water is cut at the nodes
// it has to meet rather than noded against anything.
public sealed class ZoneAssemblyService : IZoneAssemblyService
{
    // How close to the traced antimeridian a node has to lie to be taken for a point of it, in units
    // of the source map. The rim's own nodes lie on the traced line to within the noding grid, and a
    // zone boundary's end on it lies on the meridian, which the trace follows to within 1e-5 units.
    private const double RimTolerance = 0.001;

    // How near the rim every vertex of a shore has to lie for the shore to be the rim drawn again, in
    // units of the source map: the test 5c puts against the rim. Where Antarctica's coast is the
    // rim it wanders across it by up to 0.004 degrees, and 5c keeps stretches of the rim and of the
    // coast in turn along it.
    private const double RimDuplicateTolerance = 0.01;

    // How many faces whose boundary disagrees about their owner are named in the log.
    private const int MaximumReported = 20;

    private readonly IAssemblyService assemblyService;

    private readonly IProjectionService projectionService;

    private readonly IRimService rimService;

    private readonly ITopologyService topologyService;

    public ZoneAssemblyService(
        IProjectionService projectionService,
        IRimService rimService,
        ITopologyService topologyService,
        IAssemblyService assemblyService)
    {
        ArgumentNullException.ThrowIfNull(projectionService);
        ArgumentNullException.ThrowIfNull(rimService);
        ArgumentNullException.ThrowIfNull(topologyService);
        ArgumentNullException.ThrowIfNull(assemblyService);

        this.projectionService = projectionService;
        this.rimService = rimService;
        this.topologyService = topologyService;
        this.assemblyService = assemblyService;
    }

    // The coasts of land taken round the rim go in as copies, put where the land is rather than
    // where it is drawn - see AddWrapped - and once the zones are assembled each copy in them is
    // swapped back for the land's own arc.
    public IReadOnlyList<Country> Assemble(
        ZoneGraph zones,
        ArcGraph arcGraph,
        IEnumerable<Shore> shores,
        Envelope sourceEnvelope)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(shores);
        ArgumentNullException.ThrowIfNull(sourceEnvelope);

        ArcGraph sea = zones.Graph;
        Shore[] all = [.. shores];
        Dictionary<Arc, Shore> drawn = all.Where(shore => shore.LongitudeOffset.Equals(0.0))
            .ToDictionary(shore => shore.Arc);
        Arc[] landRim = [.. arcGraph.Arcs.Where(arc => arc.Kind == ArcKind.Rim)];
        foreach (Arc arc in drawn.Keys.Concat(landRim))
        {
            sea.Arcs.Add(arc);
        }

        Dictionary<Arc, Shore> wrapped = this.AddWrapped(
            sea,
            all.Where(shore => !shore.LongitudeOffset.Equals(0.0)),
            landRim,
            zones.Geographic,
            sourceEnvelope);
        foreach ((Arc copy, Shore shore) in wrapped)
        {
            drawn.Add(copy, shore);
        }

        Arc[] seaRim =
        [
            .. this.AddRim(
                sea,
                arcGraph,
                [.. landRim.Concat(drawn.Keys)],
                zones.Antimeridian.Concat(wrapped.Keys.SelectMany(arc => new[] { arc.StartNode, arc.EndNode })),
                sourceEnvelope),
        ];
        (int crossings, IReadOnlyList<string> where) = ArcCrossings.Count(
            sea.Arcs.Where(arc => zones.Boundaries.ContainsKey(arc)).Concat(seaRim),
            sea.Arcs,
            MaximumReported);
        Log.Information(
            "Built the sea graph from {Zones} zone arcs, {Shores} shores, {LandRim} stretches of the rim over land "
            + "and {SeaRim} over water, and copies of the {Wrapped} shores of land taken round the rim put where "
            + "it lies; {Crossings} crossings left that are not nodes.",
            zones.Boundaries.Count,
            drawn.Count - wrapped.Count,
            landRim.Length,
            seaRim.Length,
            wrapped.Count,
            crossings);
        if (where.Count > 0)
        {
            Log.Warning("Arcs of the sea graph crossing, among them: {Which}.", string.Join("; ", where));
        }

        int unenclosed = this.topologyService.TraceFaces(sea);
        Country[] zoneCountries = Assign(sea, zones, drawn, unenclosed);

        IReadOnlyList<Face> dropped = this.assemblyService.Assemble(sea, zoneCountries);
        foreach (IList<Shape> shapes in zoneCountries.Select(country => country.Shapes))
        {
            for (int i = 0; i < shapes.Count; ++i)
            {
                Shape shape = shapes[i];
                shapes[i] = new Shape(
                    shape.Id,
                    shape.Polygon,
                    shape.Rings.Select(
                        ring => ring.Select(
                                directed => wrapped.TryGetValue(directed.Arc, out Shore? shore)
                                    ? new DirectedArc(shore.Arc, directed.IsReversed)
                                    : directed)
                            .ToArray()));
            }
        }

        Log.Information(
            "Assembled the economic zones of {Countries} countries into {Shapes} shapes; {Dropped} faces left out.",
            zoneCountries.Length,
            zoneCountries.Sum(country => country.Shapes.Count),
            dropped.Count);

        return zoneCountries;
    }

    // Each face's owner from what bounds it, weighed by length, and the land faces taken out of the
    // graph, since the sea graph encloses the land as well and it is no part of any zone.
    private static Country[] Assign(
        ArcGraph sea,
        ZoneGraph zones,
        Dictionary<Arc, Shore> drawn,
        int unenclosed)
    {
        Dictionary<Country, Country> zoneOf = [];
        List<Face> land = [];
        List<string> disputed = [];
        int disputedCount = 0;
        double disputedArea = 0.0;
        (int Faces, double Area) zone = (0, 0.0);
        (int Faces, double Area) highSeas = (0, 0.0);
        double landArea = 0.0;
        foreach (Face face in sea.Faces)
        {
            Ballot ballot = Vote(face, zones, drawn);
            double area = GetArea(face);
            if (ballot.Choices > 1)
            {
                ++disputedCount;
                disputedArea += area;
                if (disputed.Count < MaximumReported)
                {
                    Coordinate at = face.Shell.GetCoordinates()[0];
                    disputed.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"face {face.Id} at {at.X:F2}, {at.Y:F2}, {area:G3} square units, given to {ballot.Name}"));
                }
            }

            if (ballot.IsLand)
            {
                land.Add(face);
                landArea += area;
                continue;
            }

            if (ballot.Owner == null)
            {
                highSeas = (highSeas.Faces + 1, highSeas.Area + area);
                continue;
            }

            if (!zoneOf.TryGetValue(ballot.Owner, out Country? owner))
            {
                owner = new Country(ballot.Owner.Id, ballot.Owner.Type);
                zoneOf.Add(ballot.Owner, owner);
            }

            face.Owner = owner;
            zone = (zone.Faces + 1, zone.Area + area);
        }

        foreach (Face face in land)
        {
            sea.Faces.Remove(face);
        }

        Log.Information(
            "Traced the sea graph into {Faces} faces and {Unenclosed} outlines bounding none: {Zone} faces of "
            + "{ZoneArea:0.0} square units in the zones of {Countries} countries, {HighSeas} of {HighSeasArea:0.0} on "
            + "the high seas, and {Land} of {LandArea:0.0} that are land and left out.",
            zone.Faces + highSeas.Faces + land.Count,
            unenclosed,
            zone.Faces,
            zone.Area,
            zoneOf.Count,
            highSeas.Faces,
            highSeas.Area,
            land.Count,
            landArea);
        if (disputedCount > 0)
        {
            Log.Warning(
                "{Disputed} faces of {Area:0.000} square units in all are bounded by arcs that disagree about whose "
                + "they are, and each went to whichever had the most of its boundary: {Which}.",
                disputedCount,
                disputedArea,
                string.Join("; ", disputed));
        }

        return [.. zoneOf.Values];
    }

    // The face less its holes, in the source map's square units.
    private static double GetArea(Face face)
    {
        return Math.Abs(face.Shell.GetCoordinates().GetSignedArea())
            - face.Holes.Sum(hole => Math.Abs(hole.GetCoordinates().GetSignedArea()));
    }

    private static double GetLength(Arc arc)
    {
        Coordinate[] coordinates = arc.GetFullCoordinates();
        double length = 0.0;
        for (int i = 1; i < coordinates.Length; ++i)
        {
            length += coordinates[i - 1].Distance(coordinates[i]);
        }

        return length;
    }

    // Whether every vertex of an arc lies within RimDuplicateTolerance of a line.
    private static bool IsAlong(Arc arc, Coordinate[] line)
    {
        return Array.TrueForAll(
            arc.GetFullCoordinates(),
            coordinate => Locate(line, coordinate).Distance <= RimDuplicateTolerance);
    }

    // Where along a traced line a point lies, as the segment and the fraction of it, and how far
    // off the line it is.
    private static (int Segment, double Fraction, double Distance) Locate(Coordinate[] line, Coordinate point)
    {
        (int Segment, double Fraction, double Distance) best = (0, 0.0, double.MaxValue);
        for (int i = 0; i < (line.Length - 1); ++i)
        {
            double distance = DistanceComputer.PointToSegment(point, line[i], line[i + 1]);
            if (distance < best.Distance)
            {
                LineSegment segment = new LineSegment(line[i], line[i + 1]);
                best = (i, Math.Clamp(segment.ProjectionFactor(point), 0.0, 1.0), distance);
            }
        }

        return best;
    }

    // The length of each face's boundary that speaks for each owner it could have. The boundary's
    // own left and right are as it runs in longitude and latitude, east and north, and the source
    // map's y runs down the page, which mirrors them: the face to the left of an arc in the map is
    // the zone to the right of its boundary.
    private static Ballot Vote(Face face, ZoneGraph zones, Dictionary<Arc, Shore> drawn)
    {
        Ballot ballot = new Ballot();
        foreach (DirectedArc directed in face.Holes.Prepend(face.Shell).SelectMany(ring => ring))
        {
            double length = GetLength(directed.Arc);
            if (drawn.TryGetValue(directed.Arc, out Shore? shore))
            {
                if (directed.IsReversed == shore.IsLandReversed)
                {
                    ballot.AddLand(length);
                }
                else
                {
                    ballot.Add(shore.Owner, length);
                }
            }
            else if (zones.Boundaries.TryGetValue(directed.Arc, out ZoneBoundary? boundary))
            {
                ballot.Add(directed.IsReversed ? boundary.Left : boundary.Right, length);
            }
        }

        return ballot;
    }

    // The rim over water: the antimeridian as RimService traces it, cut at every node it has to meet
    // - the ends of the rim over land, where it crosses a shore, and the ends of zone boundaries cut
    // at the antimeridian - with every stretch between two of them that lies over water kept. The
    // stretches beyond the last node at either end run on past the poles and meet nothing.
    //
    // Not a stretch some arc of the land already draws: the rim over land itself, or a shore joining
    // the same two nodes along it. Where Antarctica's coast is the rim, a point of it is as near land
    // as water, and the even-odd test called such stretches water and laid them on top of the land's:
    // 775 crossings, 18 pairs of arcs leaving their nodes at one angle, and two left after the rim
    // over land was ruled out, where the coast is the one 5c kept.
    private List<Arc> AddRim(
        ArcGraph sea,
        ArcGraph arcGraph,
        Arc[] drawnAlready,
        IEnumerable<Node> antimeridian,
        Envelope sourceEnvelope)
    {
        Arc[] landRim = [.. drawnAlready.Where(arc => arc.Kind == ArcKind.Rim)];
        ArcGraph traced = new ArcGraph(sea.CoordinateSequenceFactory);
        this.rimService.AddArcs(traced, sourceEnvelope);
        ShoreIndex shore = new ShoreIndex(arcGraph.Arcs);
        Node[] candidates =
        [
            .. landRim.SelectMany(arc => new[] { arc.StartNode, arc.EndNode }).Concat(antimeridian).Distinct(),
        ];
        ILookup<(Node, Node), Arc> joining = drawnAlready.SelectMany(
                arc => new[] { (Key: (arc.StartNode, arc.EndNode), Arc: arc), (Key: (arc.EndNode, arc.StartNode), Arc: arc) })
            .ToLookup(pair => pair.Key, pair => pair.Arc);
        List<Arc> added = [];
        foreach (Arc line in traced.Arcs)
        {
            Coordinate[] rim = line.GetFullCoordinates();
            List<(int Segment, double Fraction, Node Node)> breaks = [];
            foreach (Node node in candidates)
            {
                (int segment, double fraction, double distance) = Locate(rim, node.Coordinate);
                if (distance <= RimTolerance)
                {
                    breaks.Add((segment, fraction, node));
                }
            }

            breaks.Sort(
                (one, other) => (one.Segment, one.Fraction, one.Node.Id).CompareTo(
                    (other.Segment, other.Fraction, other.Node.Id)));
            for (int i = 1; i < breaks.Count; ++i)
            {
                (int Segment, double Fraction, Node Node) from = breaks[i - 1];
                (int Segment, double Fraction, Node Node) to = breaks[i];
                if (from.Node == to.Node)
                {
                    continue;
                }

                Coordinate[] interior =
                [
                    .. rim[(from.Segment + 1)..(to.Segment + 1)]
                        .Where(
                            coordinate => !coordinate.Equals2D(from.Node.Coordinate)
                                && !coordinate.Equals2D(to.Node.Coordinate)),
                ];
                Coordinate middle = interior.Length > 0
                    ? interior[interior.Length / 2]
                    : new Coordinate(
                        (from.Node.Coordinate.X + to.Node.Coordinate.X) / 2.0,
                        (from.Node.Coordinate.Y + to.Node.Coordinate.Y) / 2.0);
                Coordinate[] stretch = [from.Node.Coordinate, .. interior, to.Node.Coordinate];
                if (joining[(from.Node, to.Node)].Any(arc => IsAlong(arc, stretch)))
                {
                    continue;
                }

                if (shore.IsOverWater(middle))
                {
                    added.Add(sea.AddArc(ArcKind.Rim, from.Node, to.Node, sea.CoordinateSequenceFactory.Create(interior)));
                }
            }
        }

        return added;
    }

    // A copy of each shore of land taken round the rim, put where the land lies, each with the shore
    // it copies. The shore is drawn past the edge of the source map and lies at the other edge of
    // it, and its zone was measured where it lies and projected there; left out, as it was, the zone
    // took no hole for the land, and on this map Anian's zone covered Anian-23 and the far parts of
    // Anian-21, -22 and -43, and ran along the rim where they meet it the way they do, 11 segments
    // of it.
    //
    // A copy is the shore's own longitude and latitude projected into the source map, and the
    // longitude and latitude go into the record 7b takes them back from, so that it writes the
    // copy's points as the land's. Where the shore meets the rim that is exactly 180 degrees, as 7b
    // writes the rim, and the copy's node then lies on the antimeridian, where the rim over water is
    // cut at it. Copies of shores that meet at a node share a copy of the node.
    private Dictionary<Arc, Shore> AddWrapped(
        ArcGraph sea,
        IEnumerable<Shore> shores,
        Arc[] landRim,
        IDictionary<Coordinate, Coordinate> geographic,
        Envelope sourceEnvelope)
    {
        HashSet<Node> onRim = [.. landRim.SelectMany(arc => new[] { arc.StartNode, arc.EndNode })];
        Dictionary<Node, Node> nodes = [];
        Dictionary<Arc, Shore> copies = [];
        foreach (Shore shore in shores)
        {
            IReadOnlyList<Coordinate> coordinates = shore.Coordinates;
            Node start = this.GetCopy(sea, nodes, onRim, shore.Arc.StartNode, coordinates[0], geographic, sourceEnvelope);
            Node end = shore.Arc.IsClosed
                ? start
                : this.GetCopy(sea, nodes, onRim, shore.Arc.EndNode, coordinates[^1], geographic, sourceEnvelope);
            Coordinate[] interior =
            [
                .. coordinates.Skip(1)
                    .Take(coordinates.Count - 2)
                    .Select(coordinate => this.Place(coordinate, geographic, sourceEnvelope)),
            ];
            copies.Add(sea.AddArc(shore.Arc.Kind, start, end, sea.CoordinateSequenceFactory.Create(interior)), shore);
        }

        return copies;
    }

    private Node GetCopy(
        ArcGraph sea,
        Dictionary<Node, Node> nodes,
        HashSet<Node> onRim,
        Node node,
        Coordinate at,
        IDictionary<Coordinate, Coordinate> geographic,
        Envelope sourceEnvelope)
    {
        if (!nodes.TryGetValue(node, out Node? copy))
        {
            Coordinate where = at;
            if (onRim.Contains(node))
            {
                where = new Coordinate(at.X < 0.0 ? -180.0 : 180.0, at.Y);
            }

            copy = sea.AddNode(this.Place(where, geographic, sourceEnvelope));
            nodes.Add(node, copy);
        }

        return copy;
    }

    // A point given in longitude and latitude, in the source map's coordinates, with the longitude
    // and latitude recorded against it.
    private Coordinate Place(Coordinate coordinate, IDictionary<Coordinate, Coordinate> geographic, Envelope sourceEnvelope)
    {
        Coordinate placed = this.projectionService.Unscale(this.projectionService.Project(coordinate), sourceEnvelope);
        geographic.TryAdd(placed, coordinate);

        return placed;
    }

    // The lengths of a face's boundary for each owner it could have: land, the high seas, or a
    // country. The face goes to the longest.
    private sealed class Ballot
    {
        private readonly Dictionary<Country, double> countries = [];

        private double highSeas;

        private double land;

        // How many owners the boundary speaks for. More than one is a face the zone boundaries did
        // not divide where they should have, which is what a median line left loose does.
        public int Choices => this.countries.Count + (this.highSeas > 0.0 ? 1 : 0) + (this.land > 0.0 ? 1 : 0);

        public bool IsLand => (this.land > 0.0) && (this.land >= this.highSeas) && this.countries.Values.All(
            length => this.land >= length);

        public string Name => this.IsLand ? "land" : this.Owner?.Id ?? "the high seas";

        // The country with the most of the boundary, if it has more than the high seas, ties going
        // to the lower identifier so that the order the arcs were walked in decides nothing.
        public Country? Owner
        {
            get
            {
                Country? best = null;
                foreach ((Country country, double length) in this.countries)
                {
                    if ((best == null)
                        || (length > this.countries[best])
                        || (length.Equals(this.countries[best]) && (string.CompareOrdinal(country.Id, best.Id) < 0)))
                    {
                        best = country;
                    }
                }

                return (best != null) && (this.countries[best] > this.highSeas) ? best : null;
            }
        }

        public void Add(Country? country, double length)
        {
            if (country == null)
            {
                this.highSeas += length;
                return;
            }

            this.countries[country] = this.countries.GetValueOrDefault(country) + length;
        }

        public void AddLand(double length)
        {
            this.land += length;
        }
    }
}
