#pragma once
#include <lub/lub_api.h>
#include <stdbool.h>

bool lubwebxr_active(void);
bool lubwebxr_focused(void);
bool lubwebxr_view(int eye, float near_plane, float far_plane, LubXrView *out);
bool lubwebxr_input(int hand, LubXrInput *out);
bool lubwebxr_select_eye(int eye);
int lubwebxr_eye(void);
void lubwebxr_begin(void);
void lubwebxr_present(bool rendered);
