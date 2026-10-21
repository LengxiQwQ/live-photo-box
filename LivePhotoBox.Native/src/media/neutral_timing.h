#pragma once

#include "livephotobox_native.h"

// Public declaration and POD live in livephotobox_native.h. The implementation
// deliberately observes fixed source/final bytes and does not reuse the legacy
// timing DTO, whose absent numeric fields do not carry presence information.
