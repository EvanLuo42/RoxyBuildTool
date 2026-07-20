#pragma once

#ifndef ROXY_RUNTIME_API
#ifdef _WIN32
#define ROXY_RUNTIME_API __declspec(dllimport)
#else
#define ROXY_RUNTIME_API
#endif
#endif

ROXY_RUNTIME_API int roxy_runtime_value();
