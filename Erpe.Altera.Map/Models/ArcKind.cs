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
}
