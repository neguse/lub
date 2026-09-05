#pragma once
// host bridge の C API は include/lub/lub_api.h (lub_host_*)。
struct App;
void api_host_shutdown(struct App *app);
