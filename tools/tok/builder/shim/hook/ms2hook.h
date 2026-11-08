// tools/tok/builder shim (ours): the hook registrations at the end of the recovered units compile to nothing here
// (same expansion as src/hook/ms2hook.h when hooks are off).
#pragma once
#define MS2_REPLACE(va, function, glen, ghash) static_assert(true, "")
#define MS2_REPLACE_OVERLOAD(va, type, function, glen, ghash) static_assert(true, "")
#define MS2_REPLACE_METHOD(va, Class, Method, glen, ghash) static_assert(true, "")
#define MS2_REPLACE_METHOD_VIA(va, Class, Base, Method, glen, ghash) static_assert(true, "")
