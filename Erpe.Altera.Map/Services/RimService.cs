// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

using Serilog;

// Land the drawing carries past the rim. The source map's two edges are one meridian, and what
// lies beyond one of them is land that belongs just inside the other: Anian's islands run off
// the eastern edge at 51 degrees north and nothing is drawn for them on the western one. It
// used to be clamped onto the antimeridian instead, which flattened an islet lying wholly past
// the rim into a line and dropped it, and left a strip of no width along the meridian wherever
// a coast merely overshot it.
//
// Not all of it wants wrapping, and that is what makes this more than an offset. On this map
// nine shapes reach past the rim and they come in three kinds:
//
// * Anian-23 lies wholly beyond, at 180.04 to 180.38. Nothing lands on it once wrapped.
// * Anian-21, -22 and -43 cross the rim and nothing is drawn where their far part lands, so they
//   are islands the rim runs through. Anian-22 has 20 of its 29 vertices beyond it.
// * Chukcha and Mammoth Island are drawn twice, an eastern half and a western, and each half
//   overshoots the rim onto ground the other already covers: of the points sampled in the four
//   overshoots, 335 of 340, 589 of 589, 507 of 507 and 532 of 532 land on the other half. These
//   are the cut line drawn generously, and wrapping them would cover that ground twice.
//
// Antarctica's coast is a fourth kind, drawn along both edges and wandering across each by up to
// 0.004 degrees, and 5c cuts it at the rim as a line drawn twice before anything here sees it.
//
// So the land is cut at the rim, as a line in the graph like any border, and each face beyond
// it is then either taken round or left out depending on whether its far side is free. Cutting
// is what makes both possible, because it is what makes a face lie wholly on one side. On this
// map that takes 4 faces round and leaves 4 out: Anian's and the two halves each of Chukcha and
// Mammoth Island.
public sealed class RimService : IRimService
{
    // How finely the rim is traced, in degrees of latitude. A step of 0.01 is about 0.3 units of
    // the source map, and the meridian bends so gently that the chord stays within 1e-5 units of
    // the curve, a tenth of the noding grid.
    private const double LatitudeStep = 0.01;

    // How far the rim is carried on past each pole, in units of the source map, straight off
    // the edge of the map. The projection's rim ends at the corner where the meridian meets the
    // pole line, and Antarctica's coast is drawn through that corner rather than inside it: it
    // reaches the pole 0.0002 degrees past the rim on either side. A rim that stopped at the
    // corner would end inside the land and divide nothing; carried past, it leaves the land
    // across the coast along the pole line, and the stretch beyond goes with the rest of the
    // rim that lies over water. One unit is fifty times the distance it has to clear.
    private const double PoleOvershoot = 1.0;

    // The projection's own x at either pole, where the rim comes closest to the middle of the
    // map. A face that stays within it lies inside the rim whatever its latitude, which is what
    // spares the 7000 faces of the map an unprojection each.
    private const double PoleX = 1.0;

    private readonly GeometryFactory geometryFactory;

    private readonly IProjectionService projectionService;

    public RimService(GeometryFactory geometryFactory, IProjectionService projectionService)
    {
        ArgumentNullException.ThrowIfNull(geometryFactory);
        ArgumentNullException.ThrowIfNull(projectionService);

        this.geometryFactory = geometryFactory;
        this.projectionService = projectionService;
    }

    // After welding rather than with the tracers, so that no loose end can be threaded into the
    // rim in 4c: it is not a line anybody drew, and an end that reaches it has reached nothing.
    public void AddArcs(ArcGraph arcGraph, Envelope sourceEnvelope)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(sourceEnvelope);

        int steps = (int)Math.Round(180.0 / LatitudeStep);
        foreach (double longitude in new[] { -180.0, 180.0 })
        {
            List<Coordinate> line = [];
            for (int i = 0; i <= steps; ++i)
            {
                double latitude = Math.Min(-90.0 + (i * LatitudeStep), 90.0);
                line.Add(
                    this.projectionService.Unscale(
                        this.projectionService.Project(new Coordinate(longitude, latitude)),
                        sourceEnvelope));
            }

            // South is down the page, since the source map's y runs that way.
            line.Insert(0, new Coordinate(line[0].X, line[0].Y + PoleOvershoot));
            line.Add(new Coordinate(line[^1].X, line[^1].Y - PoleOvershoot));

            arcGraph.AddArc(ArcKind.Rim, arcGraph.CoordinateSequenceFactory.Create(line.ToArray()));
        }
    }

    // Which side of the rim a face lies on is asked of one point inside it, which is enough
    // because 5a has cut every face that crossed it. Where it lands once wrapped is asked of the
    // same point, carried round the globe and projected back into the source map.
    public IReadOnlyList<Face> Wrap(ArcGraph arcGraph, Envelope sourceEnvelope)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(sourceEnvelope);

        Polygon[] polygons = [.. arcGraph.Faces.Select(this.CreatePolygon)];
        List<(Face Face, Polygon Polygon, Coordinate Image)> beyond = [];
        for (int i = 0; i < polygons.Length; ++i)
        {
            if (polygons[i].IsEmpty || this.IsWithinPoles(polygons[i].EnvelopeInternal, sourceEnvelope))
            {
                continue;
            }

            Coordinate geographic = this.projectionService.Unproject(
                this.projectionService.Rescale(polygons[i].InteriorPoint.Coordinate, sourceEnvelope));
            if (Math.Abs(geographic.X) <= 180.0)
            {
                continue;
            }

            Face face = arcGraph.Faces[i];
            face.LongitudeOffset = geographic.X > 0.0 ? -360.0 : 360.0;
            Coordinate projected = this.projectionService.Project(
                new Coordinate(geographic.X + face.LongitudeOffset, geographic.Y));
            beyond.Add((face, polygons[i], this.projectionService.Unscale(projected, sourceEnvelope)));
        }

        // Ground already drawn is any face inside the rim that is not water. A lake is not
        // ground, and a face beyond the rim is not where anything is drawn yet.
        STRtree<int> index = new STRtree<int>();
        for (int i = 0; i < polygons.Length; ++i)
        {
            Face face = arcGraph.Faces[i];
            if (face.LongitudeOffset.Equals(0.0)
                && !polygons[i].IsEmpty
                && !face.Shell.All(directed => directed.Arc.Kind == ArcKind.Lake))
            {
                index.Insert(polygons[i].EnvelopeInternal, i);
            }
        }

        List<Face> dropped = [];
        double wrappedArea = 0.0;
        double droppedArea = 0.0;
        foreach ((Face face, Polygon polygon, Coordinate image) in beyond)
        {
            Point point = this.geometryFactory.CreatePoint(image);
            if (index.Query(new Envelope(image)).Any(i => polygons[i].Contains(point)))
            {
                dropped.Add(face);
                droppedArea += polygon.Area;
            }
            else
            {
                wrappedArea += polygon.Area;
            }
        }

        foreach (Face face in dropped)
        {
            arcGraph.Faces.Remove(face);
        }

        if (beyond.Count > 0)
        {
            Log.Information(
                "Took {Wrapped} faces drawn beyond the rim, {WrappedArea:0.###} square units, round to the other "
                + "side of the map, and left out {Dropped}, {DroppedArea:0.###} square units, whose far side is "
                + "ground already drawn there.",
                beyond.Count - dropped.Count,
                wrappedArea,
                dropped.Count,
                droppedArea);
        }

        return dropped;
    }

    // Every arc takes the offset of the faces either side of it, which agree for every arc but
    // the rim: the rim alone divides the land inside it from the land beyond, and 5a has put it
    // between every such pair. So a shape lies wholly on one side, and it takes its offset from
    // any arc of it that is not the rim.
    //
    // The copy is a second Arc over the same nodes and the same coordinates. Both sides of a
    // border share coordinates because they share one arc, and here they must not: the shape
    // inside the eastern rim meets it at 180 and the wrapped shape at -180. One copy is shared
    // by every wrapped shape along a stretch of rim, in either layer, so the two layers still
    // agree about it.
    public IReadOnlyDictionary<Arc, double> Twin(ArcGraph arcGraph, IEnumerable<Country> countries)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(countries);

        Dictionary<Arc, double> offsets = [];
        foreach (Face face in arcGraph.Faces)
        {
            foreach (DirectedArc directed in face.Holes.Prepend(face.Shell)
                         .SelectMany(ring => ring)
                         .Where(directed => directed.Arc.Kind != ArcKind.Rim))
            {
                offsets[directed.Arc] = face.LongitudeOffset;
            }
        }

        Dictionary<Arc, Arc> twins = [];
        int wrapped = 0;
        foreach (IList<Shape> shapes in countries.Select(country => country.Shapes))
        {
            for (int i = 0; i < shapes.Count; ++i)
            {
                Shape shape = shapes[i];
                double offset = shape.Rings.SelectMany(ring => ring)
                    .Where(directed => directed.Arc.Kind != ArcKind.Rim)
                    .Select(directed => offsets.GetValueOrDefault(directed.Arc))
                    .FirstOrDefault();
                if (offset.Equals(0.0))
                {
                    continue;
                }

                List<DirectedArc[]> rings = [];
                foreach (DirectedArc[] ring in shape.Rings)
                {
                    rings.Add(
                    [
                        .. ring.Select(
                            directed => directed.Arc.Kind != ArcKind.Rim
                                ? directed
                                : new DirectedArc(
                                    GetTwin(arcGraph, twins, offsets, directed.Arc, offset),
                                    directed.IsReversed)),
                    ]);
                }

                shapes[i] = new Shape(shape.Id, shape.Polygon, rings);
                ++wrapped;
            }
        }

        if (wrapped > 0)
        {
            Log.Information(
                "Wrapped {Shapes} shapes round the rim, with their own copy of the {Twins} rim arcs they meet the "
                + "rest of their country along.",
                wrapped,
                twins.Count);
        }

        return offsets;
    }

    private static Arc GetTwin(
        ArcGraph arcGraph,
        Dictionary<Arc, Arc> twins,
        Dictionary<Arc, double> offsets,
        Arc arc,
        double offset)
    {
        if (!twins.TryGetValue(arc, out Arc? twin))
        {
            twin = arcGraph.AddArc(ArcKind.Rim, arc.StartNode, arc.EndNode, arc.Coordinates);
            twins.Add(arc, twin);
            offsets.Add(twin, offset);
        }

        return twin;
    }

    // Whether an envelope lies between the two poles' ends of the rim, where nothing can be past
    // it. The projection's x does not depend on the source map's y, so one row will do.
    private bool IsWithinPoles(Envelope envelope, Envelope sourceEnvelope)
    {
        double east = this.projectionService.Rescale(new Coordinate(envelope.MaxX, envelope.MinY), sourceEnvelope).X;
        double west = this.projectionService.Rescale(new Coordinate(envelope.MinX, envelope.MinY), sourceEnvelope).X;

        return (east <= PoleX) && (west >= -PoleX);
    }

    // A ring with too few distinct points to close is left out, and a face whose shell is one of
    // those comes out empty: it has no inside for a point to be taken from or to land in.
    private Polygon CreatePolygon(Face face)
    {
        LinearRing? shell = this.CreateRing(face.Shell);
        if (shell == null)
        {
            return Polygon.Empty;
        }

        return this.geometryFactory.CreatePolygon(
            shell,
            [.. face.Holes.Select(hole => this.CreateRing(hole)).OfType<LinearRing>()]);
    }

    private LinearRing? CreateRing(IEnumerable<DirectedArc> ring)
    {
        List<Coordinate> coordinates = ring.GetCoordinates();

        return coordinates.Count < 3
            ? null
            : this.geometryFactory.CreateLinearRing([.. coordinates, coordinates[0]]);
    }
}
