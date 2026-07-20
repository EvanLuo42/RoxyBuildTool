#define ROXY_RUNTIME_API __declspec(dllexport)
#include "EngineRuntime.h"
#include "EngineCore.h"

int roxy_runtime_value()
{
    return roxy_core_value();
}
