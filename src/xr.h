#pragma once
#include <openxr/openxr.h>
#include <stdbool.h>
#include <vulkan/vulkan.h>

typedef struct XrEyeImage {
  VkImage image;
  VkImageView view;
  int width, height;
  int eye;
  bool first_pass;
} XrEyeImage;

bool lubxr_init(void);
bool lubxr_create_instance(const VkInstanceCreateInfo *info,
                           VkInstance *instance);
bool lubxr_physical_device(VkInstance instance, VkPhysicalDevice *physical);
bool lubxr_create_device(const VkDeviceCreateInfo *info,
                         VkPhysicalDevice physical, VkDevice *device);
bool lubxr_start(VkInstance instance, VkPhysicalDevice physical,
                 VkDevice device, uint32_t queue_family);
void lubxr_shutdown(void);
bool lubxr_poll(bool *quit);
bool lubxr_begin_frame(void);
bool lubxr_end_frame(void);
bool lubxr_select_eye(int eye);
bool lubxr_eye_image(XrEyeImage *image);
void lubxr_mark_rendered(void);
void lubxr_size(int *width, int *height);
float lubxr_frame_dt(void);
bool lubxr_focused(void);
bool lubxr_view(int eye, float near_plane, float far_plane, float *matrix);
void lubxr_view_projection(const XrPosef *pose, const XrFovf *fov,
                           float near_plane, float far_plane, float *matrix);
bool lubxr_input(int hand, float *values, bool *buttons);
