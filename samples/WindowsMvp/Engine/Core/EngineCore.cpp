#include "EngineCore.h"
#include "EngineCoreInternal.h"
#include "EngineVersion.h"

int roxy_core_value()
{
    return roxy_core_seed() + ROXY_ENGINE_VERSION;
}
