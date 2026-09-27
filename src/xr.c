#include "xr.h"
#define XR_USE_GRAPHICS_API_VULKAN
#include <SDL3/SDL.h>
#include <math.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <stdlib.h>
#include <string.h>

typedef struct Eye {
  XrSwapchain swapchain;
  XrSwapchainImageVulkanKHR *images;
  VkImageView *views;
  uint32_t count, index;
  bool acquired;
} Eye;

static struct {
  XrInstance instance;
  XrSystemId system;
  XrSession session;
  XrSpace space;
  XrActionSet actions;
  XrAction stick, trigger, grip, primary, secondary, menu;
  XrPath hands[2];
  XrView views[2];
  Eye eyes[2];
  VkDevice device;
  XrTime display_time;
  float frame_dt;
  int width, height, selected;
  unsigned rendered;
  bool running, focused, begun, visible, frame_profile, input_valid;
  PFN_xrCreateVulkanInstanceKHR create_instance;
  PFN_xrGetVulkanGraphicsDevice2KHR physical_device;
  PFN_xrCreateVulkanDeviceKHR create_device;
} x;

static bool ok(XrResult result, const char *name) {
  if (XR_SUCCEEDED(result))
    return true;
  char message[XR_MAX_RESULT_STRING_SIZE] = {0};
  if (x.instance)
    xrResultToString(x.instance, result, message);
  SDL_Log("openxr: %s failed: %d %s", name, result, message);
  return false;
}

#define XR_CHECK(call)                                                         \
  do {                                                                         \
    if (!ok((call), #call))                                                    \
      return false;                                                            \
  } while (0)

bool lubxr_init(void) {
  uint32_t count = 0;
  XR_CHECK(xrEnumerateInstanceExtensionProperties(NULL, 0, &count, NULL));
  XrExtensionProperties *extensions = calloc(count, sizeof(*extensions));
  if (!extensions)
    return false;
  for (uint32_t i = 0; i < count; ++i)
    extensions[i].type = XR_TYPE_EXTENSION_PROPERTIES;
  XrResult result =
      xrEnumerateInstanceExtensionProperties(NULL, count, &count, extensions);
  bool vulkan = false;
  for (uint32_t i = 0; XR_SUCCEEDED(result) && i < count; ++i) {
    if (strcmp(extensions[i].extensionName,
               XR_KHR_VULKAN_ENABLE2_EXTENSION_NAME) == 0)
      vulkan = true;
    if (strcmp(extensions[i].extensionName,
               "XR_VALVE_frame_controller_interaction") == 0)
      x.frame_profile = true;
  }
  free(extensions);
  if (!ok(result, "enumerate extensions") || !vulkan) {
    SDL_Log("openxr: XR_KHR_vulkan_enable2 is required");
    return false;
  }
  const char *names[] = {XR_KHR_VULKAN_ENABLE2_EXTENSION_NAME,
                         "XR_VALVE_frame_controller_interaction"};
  XrInstanceCreateInfo info = {
      .type = XR_TYPE_INSTANCE_CREATE_INFO,
      .applicationInfo = {.applicationName = "lub",
                          .engineName = "lub",
                          .apiVersion = XR_API_VERSION_1_0},
      .enabledExtensionCount = x.frame_profile ? 2 : 1,
      .enabledExtensionNames = names};
  XR_CHECK(xrCreateInstance(&info, &x.instance));
  XrInstanceProperties properties = {.type = XR_TYPE_INSTANCE_PROPERTIES};
  XR_CHECK(xrGetInstanceProperties(x.instance, &properties));
  SDL_Log("openxr: runtime=%s", properties.runtimeName);
  XrSystemGetInfo system = {.type = XR_TYPE_SYSTEM_GET_INFO,
                            .formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY};
  XR_CHECK(xrGetSystem(x.instance, &system, &x.system));
  PFN_xrGetVulkanGraphicsRequirements2KHR requirements_fn;
  XR_CHECK(xrGetInstanceProcAddr(x.instance,
                                 "xrGetVulkanGraphicsRequirements2KHR",
                                 (PFN_xrVoidFunction *)&requirements_fn));
  XrGraphicsRequirementsVulkanKHR requirements = {
      .type = XR_TYPE_GRAPHICS_REQUIREMENTS_VULKAN_KHR};
  XR_CHECK(requirements_fn(x.instance, x.system, &requirements));
  XrVersion version = XR_MAKE_VERSION(1, 3, 0);
  if (version < requirements.minApiVersionSupported ||
      version > requirements.maxApiVersionSupported) {
    SDL_Log("openxr: runtime does not support the required Vulkan 1.3 version");
    return false;
  }
  XR_CHECK(xrGetInstanceProcAddr(x.instance, "xrCreateVulkanInstanceKHR",
                                 (PFN_xrVoidFunction *)&x.create_instance));
  XR_CHECK(xrGetInstanceProcAddr(x.instance, "xrGetVulkanGraphicsDevice2KHR",
                                 (PFN_xrVoidFunction *)&x.physical_device));
  XR_CHECK(xrGetInstanceProcAddr(x.instance, "xrCreateVulkanDeviceKHR",
                                 (PFN_xrVoidFunction *)&x.create_device));
  return true;
}

bool lubxr_create_instance(const VkInstanceCreateInfo *info,
                           VkInstance *instance) {
  XrVulkanInstanceCreateInfoKHR xr = {
      .type = XR_TYPE_VULKAN_INSTANCE_CREATE_INFO_KHR,
      .systemId = x.system,
      .pfnGetInstanceProcAddr = vkGetInstanceProcAddr,
      .vulkanCreateInfo = info};
  VkResult result;
  XR_CHECK(x.create_instance(x.instance, &xr, instance, &result));
  if (result != VK_SUCCESS)
    SDL_Log("openxr: Vulkan instance failed: %d", result);
  return result == VK_SUCCESS;
}

bool lubxr_physical_device(VkInstance instance, VkPhysicalDevice *physical) {
  XrVulkanGraphicsDeviceGetInfoKHR info = {
      .type = XR_TYPE_VULKAN_GRAPHICS_DEVICE_GET_INFO_KHR,
      .systemId = x.system,
      .vulkanInstance = instance};
  XR_CHECK(x.physical_device(x.instance, &info, physical));
  return true;
}

bool lubxr_create_device(const VkDeviceCreateInfo *info,
                         VkPhysicalDevice physical, VkDevice *device) {
  XrVulkanDeviceCreateInfoKHR xr = {
      .type = XR_TYPE_VULKAN_DEVICE_CREATE_INFO_KHR,
      .systemId = x.system,
      .pfnGetInstanceProcAddr = vkGetInstanceProcAddr,
      .vulkanPhysicalDevice = physical,
      .vulkanCreateInfo = info};
  VkResult result;
  XR_CHECK(x.create_device(x.instance, &xr, device, &result));
  if (result != VK_SUCCESS)
    SDL_Log("openxr: Vulkan device failed: %d", result);
  return result == VK_SUCCESS;
}

static bool action(const char *name, XrActionType type, XrAction *out) {
  XrActionCreateInfo info = {.type = XR_TYPE_ACTION_CREATE_INFO,
                             .actionType = type,
                             .countSubactionPaths = 2,
                             .subactionPaths = x.hands};
  SDL_strlcpy(info.actionName, name, sizeof(info.actionName));
  SDL_strlcpy(info.localizedActionName, name, sizeof(info.localizedActionName));
  XR_CHECK(xrCreateAction(x.actions, &info, out));
  return true;
}

static bool bindings(bool frame) {
  const char *profile =
      frame ? "/interaction_profiles/valve/frame_controller_valve"
            : "/interaction_profiles/oculus/touch_controller";
  XrActionSuggestedBinding values[12];
  uint32_t count = 0;
  for (int hand = 0; hand < 2; ++hand) {
    const char *paths[] = {"thumbstick",
                           "trigger/value",
                           "squeeze/value",
                           hand    ? "a/click"
                           : frame ? "dpad_down/click"
                                   : "x/click",
                           hand    ? "b/click"
                           : frame ? "dpad_up/click"
                                   : "y/click",
                           hand    ? "menu/click"
                           : frame ? "view/click"
                                   : "menu/click"};
    XrAction actions[] = {x.stick,   x.trigger,   x.grip,
                          x.primary, x.secondary, x.menu};
    for (int i = 0; i < 6; ++i) {
      if (!frame && hand == 1 && i == 5)
        continue;
      char path[128];
      SDL_snprintf(path, sizeof(path), "/user/hand/%s/input/%s",
                   hand ? "right" : "left", paths[i]);
      values[count].action = actions[i];
      XR_CHECK(xrStringToPath(x.instance, path, &values[count].binding));
      ++count;
    }
  }
  XrInteractionProfileSuggestedBinding info = {
      .type = XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING,
      .countSuggestedBindings = count,
      .suggestedBindings = values};
  XR_CHECK(xrStringToPath(x.instance, profile, &info.interactionProfile));
  XR_CHECK(xrSuggestInteractionProfileBindings(x.instance, &info));
  return true;
}

static bool create_actions(void) {
  XrActionSetCreateInfo info = {.type = XR_TYPE_ACTION_SET_CREATE_INFO,
                                .actionSetName = "game",
                                .localizedActionSetName = "Game"};
  XR_CHECK(xrCreateActionSet(x.instance, &info, &x.actions));
  XR_CHECK(xrStringToPath(x.instance, "/user/hand/left", &x.hands[0]));
  XR_CHECK(xrStringToPath(x.instance, "/user/hand/right", &x.hands[1]));
  if (!action("stick", XR_ACTION_TYPE_VECTOR2F_INPUT, &x.stick) ||
      !action("trigger", XR_ACTION_TYPE_FLOAT_INPUT, &x.trigger) ||
      !action("grip", XR_ACTION_TYPE_FLOAT_INPUT, &x.grip) ||
      !action("primary", XR_ACTION_TYPE_BOOLEAN_INPUT, &x.primary) ||
      !action("secondary", XR_ACTION_TYPE_BOOLEAN_INPUT, &x.secondary) ||
      !action("menu", XR_ACTION_TYPE_BOOLEAN_INPUT, &x.menu) ||
      !bindings(false))
    return false;
  if (x.frame_profile && !bindings(true))
    return false;
  XrSessionActionSetsAttachInfo attach = {
      .type = XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO,
      .countActionSets = 1,
      .actionSets = &x.actions};
  XR_CHECK(xrAttachSessionActionSets(x.session, &attach));
  return true;
}

bool lubxr_start(VkInstance instance, VkPhysicalDevice physical,
                 VkDevice device, uint32_t family) {
  x.device = device;
  XrGraphicsBindingVulkanKHR binding = {.type =
                                            XR_TYPE_GRAPHICS_BINDING_VULKAN_KHR,
                                        .instance = instance,
                                        .physicalDevice = physical,
                                        .device = device,
                                        .queueFamilyIndex = family};
  XrSessionCreateInfo info = {.type = XR_TYPE_SESSION_CREATE_INFO,
                              .next = &binding,
                              .systemId = x.system};
  XR_CHECK(xrCreateSession(x.instance, &info, &x.session));
  XrReferenceSpaceCreateInfo space = {
      .type = XR_TYPE_REFERENCE_SPACE_CREATE_INFO,
      .referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL,
      .poseInReferenceSpace.orientation.w = 1};
  XR_CHECK(xrCreateReferenceSpace(x.session, &space, &x.space));
  if (!create_actions())
    return false;
  uint32_t count = 0;
  XrViewConfigurationView views[2] = {
      {.type = XR_TYPE_VIEW_CONFIGURATION_VIEW},
      {.type = XR_TYPE_VIEW_CONFIGURATION_VIEW}};
  XR_CHECK(xrEnumerateViewConfigurationViews(
      x.instance, x.system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 2,
      &count, views));
  if (count != 2)
    return false;
  x.width = (int)SDL_max(views[0].recommendedImageRectWidth,
                         views[1].recommendedImageRectWidth);
  x.height = (int)SDL_max(views[0].recommendedImageRectHeight,
                          views[1].recommendedImageRectHeight);
  for (int i = 0; i < 2; ++i)
    if ((uint32_t)x.width > views[i].maxImageRectWidth ||
        (uint32_t)x.height > views[i].maxImageRectHeight)
      return false;
  XR_CHECK(xrEnumerateSwapchainFormats(x.session, 0, &count, NULL));
  int64_t *formats = calloc(count, sizeof(*formats));
  if (!formats)
    return false;
  XrResult result =
      xrEnumerateSwapchainFormats(x.session, count, &count, formats);
  bool rgba = false;
  for (uint32_t i = 0; XR_SUCCEEDED(result) && i < count; ++i)
    if (formats[i] == VK_FORMAT_R8G8B8A8_UNORM)
      rgba = true;
  free(formats);
  if (!ok(result, "swapchain formats") || !rgba) {
    SDL_Log("openxr: RGBA8 UNORM swapchain is required");
    return false;
  }
  XR_CHECK(xrEnumerateEnvironmentBlendModes(
      x.instance, x.system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0,
      &count, NULL));
  XrEnvironmentBlendMode *modes = calloc(count, sizeof(*modes));
  if (!modes)
    return false;
  result = xrEnumerateEnvironmentBlendModes(
      x.instance, x.system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, count,
      &count, modes);
  bool opaque = false;
  for (uint32_t i = 0; XR_SUCCEEDED(result) && i < count; ++i)
    if (modes[i] == XR_ENVIRONMENT_BLEND_MODE_OPAQUE)
      opaque = true;
  free(modes);
  if (!ok(result, "blend modes") || !opaque)
    return false;
  for (int eye = 0; eye < 2; ++eye) {
    Eye *e = &x.eyes[eye];
    XrSwapchainCreateInfo ci = {.type = XR_TYPE_SWAPCHAIN_CREATE_INFO,
                                .usageFlags =
                                    XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT,
                                .format = VK_FORMAT_R8G8B8A8_UNORM,
                                .sampleCount = 1,
                                .width = x.width,
                                .height = x.height,
                                .faceCount = 1,
                                .arraySize = 1,
                                .mipCount = 1};
    XR_CHECK(xrCreateSwapchain(x.session, &ci, &e->swapchain));
    XR_CHECK(xrEnumerateSwapchainImages(e->swapchain, 0, &e->count, NULL));
    e->images = calloc(e->count, sizeof(*e->images));
    e->views = calloc(e->count, sizeof(*e->views));
    if (!e->images || !e->views)
      return false;
    for (uint32_t i = 0; i < e->count; ++i)
      e->images[i].type = XR_TYPE_SWAPCHAIN_IMAGE_VULKAN_KHR;
    XR_CHECK(
        xrEnumerateSwapchainImages(e->swapchain, e->count, &e->count,
                                   (XrSwapchainImageBaseHeader *)e->images));
    for (uint32_t i = 0; i < e->count; ++i) {
      VkImageViewCreateInfo vi = {
          .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
          .image = e->images[i].image,
          .viewType = VK_IMAGE_VIEW_TYPE_2D,
          .format = VK_FORMAT_R8G8B8A8_UNORM,
          .subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1}};
      if (vkCreateImageView(device, &vi, NULL, &e->views[i]) != VK_SUCCESS)
        return false;
    }
  }
  SDL_Log("openxr: stereo %dx%d per eye", x.width, x.height);
  return true;
}

void lubxr_shutdown(void) {
  for (int i = 0; i < 2; ++i) {
    Eye *e = &x.eyes[i];
    if (e->views)
      for (uint32_t j = 0; j < e->count; ++j)
        if (e->views[j])
          vkDestroyImageView(x.device, e->views[j], NULL);
    free(e->views);
    free(e->images);
    if (e->swapchain)
      xrDestroySwapchain(e->swapchain);
  }
  if (x.space)
    xrDestroySpace(x.space);
  if (x.session)
    xrDestroySession(x.session);
  if (x.actions)
    xrDestroyActionSet(x.actions);
  if (x.instance)
    xrDestroyInstance(x.instance);
  memset(&x, 0, sizeof(x));
}

bool lubxr_poll(bool *quit) {
  XrEventDataBuffer event = {.type = XR_TYPE_EVENT_DATA_BUFFER};
  XrResult result;
  while ((result = xrPollEvent(x.instance, &event)) == XR_SUCCESS) {
    if (event.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING)
      *quit = true;
    if (event.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
      const XrEventDataSessionStateChanged *state = (const void *)&event;
      SDL_Log("openxr: session state=%d", state->state);
      x.focused = state->state == XR_SESSION_STATE_FOCUSED;
      if (state->state == XR_SESSION_STATE_READY && !x.running) {
        XrSessionBeginInfo begin = {
            .type = XR_TYPE_SESSION_BEGIN_INFO,
            .primaryViewConfigurationType =
                XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO};
        if (!ok(xrBeginSession(x.session, &begin), "begin session"))
          *quit = true;
        else {
          x.running = true;
          x.display_time = 0;
        }
      } else if (state->state == XR_SESSION_STATE_STOPPING && x.running) {
        ok(xrEndSession(x.session), "end session");
        x.running = false;
      } else if (state->state == XR_SESSION_STATE_EXITING ||
                 state->state == XR_SESSION_STATE_LOSS_PENDING)
        *quit = true;
    }
    event = (XrEventDataBuffer){.type = XR_TYPE_EVENT_DATA_BUFFER};
  }
  if (result != XR_EVENT_UNAVAILABLE && !ok(result, "poll events"))
    *quit = true;
  return x.running && !*quit;
}

bool lubxr_begin_frame(void) {
  x.visible = false;
  x.rendered = 0;
  x.selected = -1;
  XrFrameWaitInfo wait = {.type = XR_TYPE_FRAME_WAIT_INFO};
  XrFrameState state = {.type = XR_TYPE_FRAME_STATE};
  XR_CHECK(xrWaitFrame(x.session, &wait, &state));
  XrFrameBeginInfo begin = {.type = XR_TYPE_FRAME_BEGIN_INFO};
  XR_CHECK(xrBeginFrame(x.session, &begin));
  x.begun = true;
  x.frame_dt = (float)((double)(x.display_time ? state.predictedDisplayTime -
                                                     x.display_time
                                               : state.predictedDisplayPeriod) /
                       1e9);
  x.frame_dt = SDL_clamp(x.frame_dt, 0.0f, 0.25f);
  x.display_time = state.predictedDisplayTime;
  XrActiveActionSet active = {.actionSet = x.actions};
  XrActionsSyncInfo sync = {.type = XR_TYPE_ACTIONS_SYNC_INFO,
                            .countActiveActionSets = 1,
                            .activeActionSets = &active};
  XrResult result = xrSyncActions(x.session, &sync);
  x.input_valid = result == XR_SUCCESS && x.focused;
  if (result != XR_SESSION_NOT_FOCUSED && !ok(result, "sync actions"))
    return false;
  if (!state.shouldRender)
    return true;
  XrViewLocateInfo locate = {.type = XR_TYPE_VIEW_LOCATE_INFO,
                             .viewConfigurationType =
                                 XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                             .displayTime = x.display_time,
                             .space = x.space};
  XrViewState views = {.type = XR_TYPE_VIEW_STATE};
  uint32_t count;
  for (int i = 0; i < 2; ++i)
    x.views[i].type = XR_TYPE_VIEW;
  XR_CHECK(xrLocateViews(x.session, &locate, &views, 2, &count, x.views));
  XrViewStateFlags valid =
      XR_VIEW_STATE_POSITION_VALID_BIT | XR_VIEW_STATE_ORIENTATION_VALID_BIT;
  if (count != 2 || (views.viewStateFlags & valid) != valid)
    return true;
  for (int i = 0; i < 2; ++i) {
    Eye *e = &x.eyes[i];
    XrSwapchainImageAcquireInfo acquire = {
        .type = XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    XR_CHECK(xrAcquireSwapchainImage(e->swapchain, &acquire, &e->index));
    e->acquired = true;
    XrSwapchainImageWaitInfo wi = {.type = XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO,
                                   .timeout = XR_INFINITE_DURATION};
    XR_CHECK(xrWaitSwapchainImage(e->swapchain, &wi));
  }
  x.visible = true;
  return true;
}

bool lubxr_end_frame(void) {
  if (!x.begun)
    return false;
  bool submit = x.visible && x.rendered == 3;
  bool success = true;
  XrCompositionLayerProjectionView views[2] = {0};
  for (int i = 0; i < 2; ++i) {
    Eye *e = &x.eyes[i];
    if (e->acquired) {
      XrSwapchainImageReleaseInfo release = {
          .type = XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
      if (!ok(xrReleaseSwapchainImage(e->swapchain, &release), "release image"))
        submit = success = false;
      e->acquired = false;
    }
    views[i] = (XrCompositionLayerProjectionView){
        .type = XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW,
        .pose = x.views[i].pose,
        .fov = x.views[i].fov,
        .subImage = {.swapchain = e->swapchain,
                     .imageRect.extent = {x.width, x.height}}};
  }
  XrCompositionLayerProjection layer = {
      .type = XR_TYPE_COMPOSITION_LAYER_PROJECTION,
      .space = x.space,
      .viewCount = 2,
      .views = views};
  const XrCompositionLayerBaseHeader *layers[] = {(const void *)&layer};
  XrFrameEndInfo info = {.type = XR_TYPE_FRAME_END_INFO,
                         .displayTime = x.display_time,
                         .environmentBlendMode =
                             XR_ENVIRONMENT_BLEND_MODE_OPAQUE,
                         .layerCount = submit ? 1 : 0,
                         .layers = layers};
  success = ok(xrEndFrame(x.session, &info), "end frame") && success;
  x.begun = x.visible = false;
  return success;
}

bool lubxr_select_eye(int eye) {
  if (!x.visible || eye < 0 || eye > 1)
    return false;
  x.selected = eye;
  return true;
}

bool lubxr_eye_image(XrEyeImage *image) {
  if (!x.visible || x.selected < 0)
    return false;
  Eye *e = &x.eyes[x.selected];
  *image = (XrEyeImage){e->images[e->index].image,
                        e->views[e->index],
                        x.width,
                        x.height,
                        x.selected,
                        (x.rendered & (1u << x.selected)) == 0};
  return true;
}

void lubxr_mark_rendered(void) {
  if (x.visible && x.selected >= 0)
    x.rendered |= 1u << x.selected;
}
void lubxr_size(int *width, int *height) {
  *width = x.width;
  *height = x.height;
}
float lubxr_frame_dt(void) { return x.frame_dt; }
bool lubxr_focused(void) { return x.focused; }

bool lubxr_view(int eye, float near_plane, float far_plane, float *matrix) {
  if (!x.visible || eye < 0 || eye > 1)
    return false;
  const XrView *v = &x.views[eye];
  lubxr_view_projection(&v->pose, &v->fov, near_plane, far_plane, matrix);
  return true;
}

void lubxr_view_projection(const XrPosef *pose, const XrFovf *fov,
                           float near_plane, float far_plane, float *matrix) {
  XrQuaternionf q = pose->orientation;
  float a = q.x, b = q.y, c = q.z, d = q.w;
  float view[16] = {1 - 2 * (b * b + c * c),
                    2 * (a * b + c * d),
                    2 * (a * c - b * d),
                    0,
                    2 * (a * b - c * d),
                    1 - 2 * (a * a + c * c),
                    2 * (b * c + a * d),
                    0,
                    2 * (a * c + b * d),
                    2 * (b * c - a * d),
                    1 - 2 * (a * a + b * b),
                    0,
                    0,
                    0,
                    0,
                    1};
  for (int row = 0; row < 3; ++row)
    view[row * 4 + 3] = -(view[row * 4] * pose->position.x +
                          view[row * 4 + 1] * pose->position.y +
                          view[row * 4 + 2] * pose->position.z);
  float l = tanf(fov->angleLeft), r = tanf(fov->angleRight);
  float u = tanf(fov->angleUp), bottom = tanf(fov->angleDown);
  float projection[16] = {2 / (r - l),
                          0,
                          (r + l) / (r - l),
                          0,
                          0,
                          2 / (u - bottom),
                          (u + bottom) / (u - bottom),
                          0,
                          0,
                          0,
                          -far_plane / (far_plane - near_plane),
                          -far_plane * near_plane / (far_plane - near_plane),
                          0,
                          0,
                          -1,
                          0};
  for (int row = 0; row < 4; ++row)
    for (int col = 0; col < 4; ++col) {
      float value = 0;
      for (int k = 0; k < 4; ++k)
        value += projection[row * 4 + k] * view[k * 4 + col];
      matrix[row * 4 + col] = value;
    }
}

bool lubxr_input(int hand, float *values, bool *buttons) {
  if (!x.input_valid || !x.begun || hand < 0 || hand > 1)
    return false;
  XrActionStateGetInfo info = {.type = XR_TYPE_ACTION_STATE_GET_INFO,
                               .action = x.stick,
                               .subactionPath = x.hands[hand]};
  XrActionStateVector2f stick = {.type = XR_TYPE_ACTION_STATE_VECTOR2F};
  XR_CHECK(xrGetActionStateVector2f(x.session, &info, &stick));
  bool active = stick.isActive;
  if (stick.isActive) {
    values[0] = stick.currentState.x;
    values[1] = stick.currentState.y;
  }
  XrAction floats[] = {x.trigger, x.grip};
  for (int i = 0; i < 2; ++i) {
    info.action = floats[i];
    XrActionStateFloat state = {.type = XR_TYPE_ACTION_STATE_FLOAT};
    XR_CHECK(xrGetActionStateFloat(x.session, &info, &state));
    if (state.isActive) {
      values[i + 2] = state.currentState;
      active = true;
    }
  }
  XrAction booleans[] = {x.primary, x.secondary, x.menu};
  for (int i = 0; i < 3; ++i) {
    info.action = booleans[i];
    XrActionStateBoolean state = {.type = XR_TYPE_ACTION_STATE_BOOLEAN};
    XR_CHECK(xrGetActionStateBoolean(x.session, &info, &state));
    if (state.isActive) {
      buttons[i] = state.currentState;
      active = true;
    }
  }
  return active;
}
