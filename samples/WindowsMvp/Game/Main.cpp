#include "EngineRuntime.h"

int main()
{
    return roxy_runtime_value() == 42 ? 0 : 1;
}
