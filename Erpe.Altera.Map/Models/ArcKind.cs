// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

public enum ArcKind
{
    Border,
    Substate,

    // The seaward edge of the land.
    Coast,

    // The edge of an inland sea. Kept apart from Coast because that is what tells a lake
    // from an enclave once the faces are traced: a face the lake shore alone bounds is
    // water, whereas one with any border in it is territory, however it happens to nest.
    Lake,

    // The antimeridian, where the map's two edges are one meridian: the line along which land
    // drawn past the rim is cut off and taken round to the other side. Not drawn but computed,
    // and kept only where it crosses land, so that it divides a face into the part inside the
    // rim and the part beyond it. The one kind of arc 7a never dissolves, since once the far
    // part is wrapped the two sides of it are 360 degrees apart.
    Rim,
}
