#pragma once
#include <stdint.h>
#ifdef _WIN32
#define FFSK_API __declspec(dllexport)
#define FFSK_CALL __cdecl
#else
#define FFSK_API __attribute__((visibility("default")))
#define FFSK_CALL
#endif

// ABI version 1, x64, IEEE float, C cdecl. All DTOs have 4-byte packing.
// Worlds are independently owned; serialize access to each world.
#ifdef __cplusplus
extern "C" {
#endif
#pragma pack(push, 4)
typedef struct ffsk_vec3 { float x, y, z; } ffsk_vec3;
typedef struct ffsk_quat { float x, y, z, w; } ffsk_quat;
typedef struct ffsk_transform { ffsk_vec3 position; ffsk_quat rotation; } ffsk_transform;
typedef struct ffsk_body_desc {
    int32_t shape; // 0 sphere, 1 box half extents, 2 Y-axis capsule radius/cylinder height
    int32_t mode;  // 0 animated kinematic, 1/2 dynamic (bone-output policy belongs to baker)
    uint32_t collision_group; // zero-based group 0..15
    uint32_t collision_mask;  // raw PMX mask: set bit means collision allowed
    ffsk_vec3 size;
    ffsk_vec3 position;
    ffsk_quat rotation;
    float mass, linear_damping, angular_damping, restitution, friction, collision_margin;
} ffsk_body_desc;
// Spring2 in raw PMX coordinates: RO_XZY, damping .5; angular limits stay raw.
typedef struct ffsk_joint_desc {
    int32_t body_a, body_b;
    ffsk_vec3 position;
    ffsk_quat rotation;
    ffsk_vec3 linear_lower, linear_upper, angular_lower, angular_upper;
    ffsk_vec3 linear_spring, angular_spring;
} ffsk_joint_desc;
#pragma pack(pop)

FFSK_API int32_t FFSK_CALL ffsk_abi_version(void);
FFSK_API const char* FFSK_CALL ffsk_engine_version(void);
FFSK_API const char* FFSK_CALL ffsk_last_error(void); // thread-local, overwritten on next call
FFSK_API void* FFSK_CALL ffsk_world_create(ffsk_vec3 gravity, int32_t solver_iterations);
FFSK_API void FFSK_CALL ffsk_world_destroy(void* world);
FFSK_API int32_t FFSK_CALL ffsk_body_add(void* world, const ffsk_body_desc* description, int32_t* body_id);
FFSK_API int32_t FFSK_CALL ffsk_joint_add(void* world, const ffsk_joint_desc* description, int32_t* joint_id);
FFSK_API int32_t FFSK_CALL ffsk_joint_add_collision_option(void* world, const ffsk_joint_desc* description, int32_t disable_collision, int32_t* joint_id);
// Adds a six-axis-free, spring-free Generic helper, retaining Blender's island connection.
FFSK_API int32_t FFSK_CALL ffsk_collision_disable_pair(void* world, int32_t body_a, int32_t body_b);
FFSK_API int32_t FFSK_CALL ffsk_body_set_transform(void* world, int32_t body_id, const ffsk_transform* transform, int32_t reset_velocity);
FFSK_API int32_t FFSK_CALL ffsk_body_get_transform(void* world, int32_t body_id, ffsk_transform* transform);
FFSK_API int32_t FFSK_CALL ffsk_body_set_position(void* world, int32_t body_id, ffsk_vec3 position);
FFSK_API int32_t FFSK_CALL ffsk_world_step(void* world, float seconds);
#ifdef __cplusplus
}
#endif
