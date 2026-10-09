#include "SkirtBullet.h"
#include "btBulletDynamicsCommon.h"
#include "BulletDynamics/ConstraintSolver/btGeneric6DofSpring2Constraint.h"
#include "BulletDynamics/ConstraintSolver/btGeneric6DofConstraint.h"
#include <cmath>
#include <cstdio>
#include <memory>
#include <vector>
#include <unordered_set>

static thread_local char error_text[256] = {};
static int fail(const char* message) { std::snprintf(error_text, sizeof(error_text), "%s", message); return -1; }
static void clear_error() { error_text[0] = 0; }
static bool finite(float value) { return std::isfinite(value) && std::abs(value) < 1e8f; }
static bool valid(ffsk_vec3 v) { return finite(v.x) && finite(v.y) && finite(v.z); }
static bool valid(ffsk_quat q) { return finite(q.x) && finite(q.y) && finite(q.z) && finite(q.w) && q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w > 1e-12f; }
static btVector3 vec(ffsk_vec3 v) { return {v.x,v.y,v.z}; }
static btQuaternion quat(ffsk_quat q) { btQuaternion result(q.x,q.y,q.z,q.w); result.normalize(); return result; }
static btTransform transform(ffsk_vec3 p, ffsk_quat q) { return btTransform(quat(q),vec(p)); }
static ffsk_transform exported(const btTransform& t) {
    const auto& p=t.getOrigin(); const auto& q=t.getRotation();
    return {{p.x(),p.y(),p.z()},{q.x(),q.y(),q.z(),q.w()}};
}

struct World {
    btDefaultCollisionConfiguration configuration;
    btCollisionDispatcher dispatcher{&configuration};
    btDbvtBroadphase broadphase;
    btSequentialImpulseConstraintSolver solver;
    btDiscreteDynamicsWorld dynamics{&dispatcher,&broadphase,&solver,&configuration};
    std::vector<std::unique_ptr<btCollisionShape>> shapes;
    std::vector<std::unique_ptr<btDefaultMotionState>> motions;
    std::vector<std::unique_ptr<btRigidBody>> bodies;
    std::vector<std::unique_ptr<btTypedConstraint>> joints;
    std::vector<std::unique_ptr<btTypedConstraint>> collision_helpers;
    std::unordered_set<uint64_t> ignored_pairs;
    ~World() {
        for (auto& helper:collision_helpers) dynamics.removeConstraint(helper.get());
        for (auto& joint:joints) dynamics.removeConstraint(joint.get());
        for (auto& body:bodies) dynamics.removeRigidBody(body.get());
    }
    btRigidBody* body(int id) { return id>=0 && static_cast<size_t>(id)<bodies.size() ? bodies[id].get() : nullptr; }
};

static_assert(sizeof(ffsk_vec3)==12 && sizeof(ffsk_quat)==16 && sizeof(ffsk_transform)==28, "transform ABI mismatch");
static_assert(sizeof(ffsk_body_desc)==80 && sizeof(ffsk_joint_desc)==108, "description ABI mismatch");

extern "C" {
int32_t FFSK_CALL ffsk_abi_version() { return 1; }
const char* FFSK_CALL ffsk_engine_version() { return "Bullet 3.25 / 2c204c49e56ed15ec5fcfa71d199ab6d6570b3f5 / ffsk ABI 1"; }
const char* FFSK_CALL ffsk_last_error() { return error_text; }
void* FFSK_CALL ffsk_world_create(ffsk_vec3 gravity,int32_t iterations) {
    clear_error();
    if (!valid(gravity) || iterations<1 || iterations>4096) { fail("Invalid world gravity or iterations."); return nullptr; }
    try {
        auto result=std::make_unique<World>(); result->dynamics.setGravity(vec(gravity));
        auto& info=result->dynamics.getSolverInfo(); info.m_numIterations=iterations;
        info.m_solverMode &= ~SOLVER_RANDMIZE_ORDER;
        // Blender's rigid-body world explicitly defaults this scene flag off.
        info.m_splitImpulse = false;
        // No deactivation or wall-clock-dependent tick state participates in a bake.
        return result.release();
    } catch (...) { fail("Could not allocate Bullet world."); return nullptr; }
}
void FFSK_CALL ffsk_world_destroy(void* world) { delete static_cast<World*>(world); }
int32_t FFSK_CALL ffsk_body_add(void* world,const ffsk_body_desc* d,int32_t* id) {
    clear_error(); auto* w=static_cast<World*>(world);
    if (!w || !d || !id) return fail("Null body argument.");
    if (w->bodies.size()>=8192 || d->shape<0 || d->shape>2 || d->mode<0 || d->mode>2 || d->collision_group>15 || d->collision_mask>65535 ||
        !valid(d->size) || !valid(d->position) || !valid(d->rotation) || d->size.x<=0 || d->size.y<0 || d->size.z<0 ||
        !finite(d->mass) || d->mass<0 || !finite(d->linear_damping) || !finite(d->angular_damping) || !finite(d->restitution) || !finite(d->friction) ||
        !finite(d->collision_margin) || d->collision_margin<0 || (d->shape==1 && (d->size.y<=0 || d->size.z<=0))) return fail("Invalid body description.");
    try {
        std::unique_ptr<btCollisionShape> shape;
        if (d->shape==0) shape=std::make_unique<btSphereShape>(d->size.x);
        else if (d->shape==1) shape=std::make_unique<btBoxShape>(vec(d->size));
        else shape=std::make_unique<btCapsuleShape>(d->size.x,d->size.y);
        shape->setMargin(d->collision_margin);
        auto pose=transform(d->position,d->rotation);
        auto motion=std::make_unique<btDefaultMotionState>(pose);
        const float mass=d->mode==0 ? 0.0f : d->mass;
        btVector3 inertia(0,0,0); if (mass>0) shape->calculateLocalInertia(mass,inertia);
        btRigidBody::btRigidBodyConstructionInfo info(mass,motion.get(),shape.get(),inertia);
        info.m_linearDamping=d->linear_damping; info.m_angularDamping=d->angular_damping;
        info.m_restitution=d->restitution; info.m_friction=d->friction;
        auto body=std::make_unique<btRigidBody>(info);
        if (d->mode==0) body->setCollisionFlags((body->getCollisionFlags() & ~btCollisionObject::CF_STATIC_OBJECT) | btCollisionObject::CF_KINEMATIC_OBJECT);
        body->setActivationState(DISABLE_DEACTIVATION);
        // Reserve first so allocation failure cannot leave the world with a
        // dangling registered body pointer.
        w->shapes.reserve(w->shapes.size()+1); w->motions.reserve(w->motions.size()+1); w->bodies.reserve(w->bodies.size()+1);
        *id=static_cast<int32_t>(w->bodies.size());
        w->dynamics.addRigidBody(body.get(),static_cast<short>(1u<<d->collision_group),static_cast<short>(d->collision_mask));
        w->shapes.push_back(std::move(shape)); w->motions.push_back(std::move(motion)); w->bodies.push_back(std::move(body));
        return 0;
    } catch (...) { return fail("Could not allocate Bullet body."); }
}
int32_t FFSK_CALL ffsk_joint_add(void* world,const ffsk_joint_desc* d,int32_t* id) {
    return ffsk_joint_add_collision_option(world,d,0,id);
}
int32_t FFSK_CALL ffsk_joint_add_collision_option(void* world,const ffsk_joint_desc* d,int32_t disable_collision,int32_t* id) {
    clear_error(); auto* w=static_cast<World*>(world);
    if (!w || !d || !id) return fail("Null joint argument.");
    auto* a=w->body(d->body_a); auto* b=w->body(d->body_b);
    if (!a || !b || a==b || w->joints.size()>=32768 || !valid(d->position) || !valid(d->rotation) || !valid(d->linear_lower) || !valid(d->linear_upper) ||
        !valid(d->angular_lower) || !valid(d->angular_upper) || !valid(d->linear_spring) || !valid(d->angular_spring)) return fail("Invalid joint description.");
    try {
        auto pose=transform(d->position,d->rotation);
        // Blender's Spring2 RO_XYZ in reflected X,Z,Y coordinates is RO_XZY
        // in this raw PMX X,Y,Z world. Keep the original PMX angular limits;
        // reflecting to Blender would swap Y/Z, negate angles, and swap bounds.
        auto joint=std::make_unique<btGeneric6DofSpring2Constraint>(*a,*b,a->getWorldTransform().inverse()*pose,b->getWorldTransform().inverse()*pose,RO_XZY);
        joint->setLinearLowerLimit(vec(d->linear_lower)); joint->setLinearUpperLimit(vec(d->linear_upper));
        joint->setAngularLowerLimit(vec(d->angular_lower)); joint->setAngularUpperLimit(vec(d->angular_upper));
        auto linear=vec(d->linear_spring); auto angular=vec(d->angular_spring);
        for (int axis=0;axis<6;axis++) {
            auto stiffness=axis<3?linear[axis]:angular[axis-3];
            if (stiffness<0) return fail("Negative joint spring stiffness.");
            // MMD Tools enables all Spring2 axes, including zero stiffness:
            // their nonzero damping still constrains relative velocity.
            joint->enableSpring(axis,true); joint->setStiffness(axis,stiffness); joint->setDamping(axis,0.5f);
        }
        joint->setEquilibriumPoint(); w->joints.reserve(w->joints.size()+1);
        *id=static_cast<int32_t>(w->joints.size());
        w->dynamics.addConstraint(joint.get(),disable_collision!=0); w->joints.push_back(std::move(joint)); return 0;
    } catch (...) { return fail("Could not allocate Bullet joint."); }
}
int32_t FFSK_CALL ffsk_collision_disable_pair(void* world,int32_t body_a,int32_t body_b) {
    clear_error(); auto* w=static_cast<World*>(world);
    auto* a=w?w->body(body_a):nullptr; auto* b=w?w->body(body_b):nullptr;
    if (!a || !b || a==b || w->collision_helpers.size()>=32768) return fail("Invalid noncollision pair.");
    const auto lo=static_cast<uint32_t>(body_a<body_b?body_a:body_b);
    const auto hi=static_cast<uint32_t>(body_a<body_b?body_b:body_a);
    const uint64_t key=(static_cast<uint64_t>(lo)<<32)|hi;
    if (w->ignored_pairs.count(key)) return 0;
    try {
        const auto origin=btTransform::getIdentity();
        auto helper=std::make_unique<btGeneric6DofConstraint>(*a,*b,a->getWorldTransform().inverse()*origin,b->getWorldTransform().inverse()*origin,true);
        // Lower > upper leaves every axis free. Generic has no spring rows,
        // so the helper only suppresses contact and links simulation islands.
        helper->setLinearLowerLimit(btVector3(0,0,0)); helper->setLinearUpperLimit(btVector3(-1,-1,-1));
        helper->setAngularLowerLimit(btVector3(0,0,0)); helper->setAngularUpperLimit(btVector3(-1,-1,-1));
        w->collision_helpers.reserve(w->collision_helpers.size()+1); w->ignored_pairs.insert(key);
        w->dynamics.addConstraint(helper.get(),true); w->collision_helpers.push_back(std::move(helper));
        // Pair filtering can also be configured after contact has occurred;
        // remove its cached algorithm/manifolds before the next solver step.
        w->broadphase.getOverlappingPairCache()->cleanProxyFromPairs(a->getBroadphaseHandle(),&w->dispatcher);
        w->broadphase.getOverlappingPairCache()->cleanProxyFromPairs(b->getBroadphaseHandle(),&w->dispatcher);
        return 0;
    } catch (...) { return fail("Could not allocate noncollision helper."); }
}
int32_t FFSK_CALL ffsk_body_set_transform(void* world,int32_t id,const ffsk_transform* value,int32_t reset_velocity) {
    clear_error(); auto* w=static_cast<World*>(world); auto* b=w?w->body(id):nullptr;
    if (!b || !value || !valid(value->position) || !valid(value->rotation)) return fail("Invalid body transform.");
    auto pose=transform(value->position,value->rotation);
    b->setWorldTransform(pose); b->updateInertiaTensor(); b->getMotionState()->setWorldTransform(pose);
    // Bullet derives animated-body velocity from the previous interpolation
    // transform in saveKinematicState. Preserve it during ordinary animation.
    if (!b->isKinematicObject() || reset_velocity) b->setInterpolationWorldTransform(pose);
    if (reset_velocity) { b->setLinearVelocity(btVector3(0,0,0)); b->setAngularVelocity(btVector3(0,0,0)); b->clearForces(); }
    b->activate(true); w->dynamics.updateSingleAabb(b); return 0;
}
int32_t FFSK_CALL ffsk_body_set_position(void* world,int32_t id,ffsk_vec3 p) {
    clear_error(); auto* w=static_cast<World*>(world); auto* b=w?w->body(id):nullptr;
    if (!b || !valid(p)) return fail("Invalid body position.");
    auto pose=b->getWorldTransform(); pose.setOrigin(vec(p));
    b->setWorldTransform(pose); b->updateInertiaTensor(); b->getMotionState()->setWorldTransform(pose);
    if (!b->isKinematicObject()) b->setInterpolationWorldTransform(pose);
    b->activate(true); w->dynamics.updateSingleAabb(b); return 0;
}
int32_t FFSK_CALL ffsk_body_get_transform(void* world,int32_t id,ffsk_transform* value) {
    clear_error(); auto* w=static_cast<World*>(world); auto* b=w?w->body(id):nullptr;
    if (!b || !value) return fail("Invalid body output.");
    *value=exported(b->getWorldTransform());
    if (!valid(value->position) || !valid(value->rotation)) return fail("Bullet produced nonfinite transform.");
    return 0;
}
int32_t FFSK_CALL ffsk_world_step(void* world,float seconds) {
    clear_error(); auto* w=static_cast<World*>(world);
    if (!w || !finite(seconds) || seconds<=0 || seconds>1) return fail("Invalid fixed physics step.");
    try { w->dynamics.stepSimulation(seconds,0,seconds); return 0; }
    catch (...) { return fail("Bullet fixed physics step failed."); }
}
}
