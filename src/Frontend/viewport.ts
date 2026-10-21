import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { TransformControls } from 'three/addons/controls/TransformControls.js';
import type { Device, Pose, RuntimeSnapshot, CaptureWork } from './types';

THREE.Object3D.DEFAULT_UP.set(0, 0, 1);
const deg = Math.PI / 180;
const orange = new THREE.MeshStandardMaterial({ color: 0xe8a140, roughness: 0.38, metalness: 0.4 });
const metal = new THREE.MeshStandardMaterial({ color: 0x85929e, roughness: 0.4, metalness: 0.65 });
const dark = new THREE.MeshStandardMaterial({ color: 0x252e3a, roughness: 0.5, metalness: 0.45 });
const teal = new THREE.MeshStandardMaterial({ color: 0x48bcb7, roughness: 0.4, metalness: 0.4 });
const boxMaterial = new THREE.MeshStandardMaterial({ color: 0x8597c7, roughness: 0.5, metalness: 0.15 });
const lensMaterial = new THREE.MeshBasicMaterial({ color: 0x276581 });

function mesh(geometry: THREE.BufferGeometry, material: THREE.Material, position: number[]) {
  const object = new THREE.Mesh(geometry, material);
  object.position.set(...position as [number, number, number]);
  return object;
}
function cylinder(radius: number, length: number, position: number[], material: THREE.Material = dark) {
  const object = mesh(new THREE.CylinderGeometry(radius, radius, length, 32), material, position);
  object.rotation.x = Math.PI / 2;
  return object;
}
function frameLabel(text: string) {
  const canvas = document.createElement('canvas'); canvas.width = 96; canvas.height = 64;
  const context = canvas.getContext('2d')!;
  context.fillStyle = '#a5f4d7'; context.font = 'bold 42px Segoe UI'; context.textAlign = 'center';
  context.fillText(text, 48, 45);
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: new THREE.CanvasTexture(canvas), depthTest: false }));
  sprite.scale.set(42, 28, 1); sprite.position.set(0, 0, 94); return sprite;
}
function robotModel(device: Device, photo = false) {
  const group = new THREE.Group();
  const g = device.robot!.geometry;
  const diameters = [g.joint1Diameter, g.joint2Diameter, g.joint3Diameter, g.joint4Diameter, g.joint5Diameter, g.joint6Diameter];
  const plate = Math.min(30, g.baseHeight / 3);
  group.add(cylinder(diameters[0] * 1.05, plate, [0, 0, plate / 2]),
    cylinder(diameters[0] * 0.73, g.baseHeight - plate, [0, 0, (g.baseHeight + plate) / 2], orange));
  const count = device.robot!.jointPositions.length;
  const joints = Array.from({ length: count }, (_, i) => {
    const pivot = new THREE.Group(); pivot.matrixAutoUpdate = false;
    const radius = diameters[i] / 2;
    pivot.add(mesh(new THREE.SphereGeometry(radius, 24, 18), dark, [0, 0, 0]));
    const cap = cylinder(radius * 0.66, radius * 0.18, [0, -radius * 0.88, 0], metal);
    cap.rotation.x = 0; pivot.add(cap); group.add(pivot); return pivot;
  });
  const rods = Array.from({ length: count }, (_, i) => {
    const radius = Math.min(diameters[i], diameters[Math.min(i + 1, count - 1)]) * 0.38;
    const rod = new THREE.Mesh(new THREE.CylinderGeometry(radius, radius, 1, 24), orange);
    group.add(rod); return rod;
  });
  const flange = new THREE.Group(); flange.matrixAutoUpdate = false;
  flange.add(cylinder(diameters[count - 1] * 0.42, 15, [0, 0, 0], metal));
  if (!photo) flange.add(new THREE.AxesHelper(80), frameLabel('F'));
  const tool = new THREE.Group(); tool.matrixAutoUpdate = false;
  if (!photo) tool.add(new THREE.AxesHelper(75), mesh(new THREE.SphereGeometry(8, 12, 8), teal, [0, 0, 0]), frameLabel('T'));
  group.add(flange, tool);
  group.userData.visual = { joints, rods, flange, tool };
  return group;
}
function cameraModel(d: Device, photo = false, ownCamera = false) {
  const group = new THREE.Group();
  if (ownCamera) return group;
  group.add(mesh(new THREE.BoxGeometry(72, 55, 54), teal, [0, 0, -38]),
    cylinder(23, 30, [0, 0, -4]), cylinder(19, 3, [0, 0, 13], metal));
  group.add(cylinder(15, 4, [0, 0, 15], lensMaterial));
  if (photo) return group;
  const p = d.parameters;
  const z = 220;
  const corners = [[-p.cx / p.fx * z, -p.cy / p.fy * z, z],
    [(p.width - p.cx) / p.fx * z, -p.cy / p.fy * z, z],
    [(p.width - p.cx) / p.fx * z, (p.height - p.cy) / p.fy * z, z],
    [-p.cx / p.fx * z, (p.height - p.cy) / p.fy * z, z]];
  const lines: number[] = [];
  for (let i = 0; i < 4; i++) lines.push(0, 0, 18, ...corners[i], ...corners[i], ...corners[(i + 1) % 4]);
  const wire = new THREE.LineSegments(new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(lines, 3)),
    new THREE.LineBasicMaterial({ color: 0x4faaa8, transparent: true, opacity: 0.55 }));
  group.add(wire);
  return group;
}

function calibrationModel(d: Device) {
  const group = new THREE.Group();
  const p = d.parameters;
  const white = new THREE.MeshBasicMaterial({ color: 0xffffff, side: THREE.DoubleSide });
  const black = new THREE.MeshBasicMaterial({ color: 0x080808, side: THREE.DoubleSide, polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -1 });
  // The pattern lies in local XY; its centre is exactly the object's datum at Z=0.
  if (d.typeId === 'calibration.board') group.add(mesh(new THREE.BoxGeometry(p.sizeX, p.sizeY, p.sizeZ), white, [0, 0, -p.sizeZ / 2 - 0.02]));
  group.add(mesh(new THREE.PlaneGeometry(p.sizeX, p.sizeY), white, [0, 0, 0]));
  const radius = Math.min(p.sizeX, p.sizeY) / (d.typeId === 'calibration.board' ? 18 : 6);
  const offsets = d.typeId === 'calibration.board' ? [-1, 0, 1] : [0];
  for (const y of offsets) for (const x of offsets) {
    group.add(mesh(new THREE.CircleGeometry(radius, 96), black, [x * p.sizeX / 3, y * p.sizeY / 3, 0]));
  }
  group.userData.ownedMaterials = [white, black];
  return group;
}

function disposeGraph(root: THREE.Object3D) {
  root.traverse(object => {
    if (object.userData.ownedMaterials) for (const material of object.userData.ownedMaterials as THREE.Material[]) material.dispose();
    if (object instanceof THREE.Mesh || object instanceof THREE.Line) object.geometry.dispose();
    if (object instanceof THREE.Line) {
      const material = object.material;
      if (Array.isArray(material)) material.forEach(m => m.dispose()); else material.dispose();
    }
    if (object instanceof THREE.Sprite) { object.material.map?.dispose(); object.material.dispose(); }
  });
}
function applyRuntime(objects: Map<string, THREE.Group>, runtime: RuntimeSnapshot) {
  for (const state of runtime.robots) {
    const object = objects.get(state.deviceId);
    if (!object || object.userData.poseId === state.poseId) continue;
    object.userData.poseId = state.poseId;
    const visual = object.userData.visual as { joints: THREE.Group[]; rods: THREE.Mesh[]; flange: THREE.Group; tool: THREE.Group };
    state.jointMatrices.forEach((matrix, index) => {
      visual.joints[index].matrix.fromArray(matrix); visual.joints[index].matrixWorldNeedsUpdate = true;
      const start = new THREE.Vector3().fromArray(matrix, 12);
      const end = new THREE.Vector3().fromArray(index < state.jointMatrices.length - 1 ? state.jointMatrices[index + 1] : state.flangeBase.matrix, 12);
      const delta = end.clone().sub(start);
      const rod = visual.rods[index]; rod.position.copy(start.clone().add(end).multiplyScalar(0.5));
      rod.scale.set(1, delta.length(), 1);
      rod.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), delta.normalize());
    });
    visual.flange.matrix.fromArray(state.flangeBase.matrix); visual.flange.matrixWorldNeedsUpdate = true;
    visual.tool.matrix.fromArray(state.tcpBase.matrix); visual.tool.matrixWorldNeedsUpdate = true;
  }
}
function populateGraph(root: THREE.Group, objects: Map<string, THREE.Group>, devices: Device[], runtime?: RuntimeSnapshot, photoCameraId?: string) {
  for (const d of devices) {
    const object = d.robot ? robotModel(d, !!photoCameraId) : d.typeId === 'camera.pinhole' ? cameraModel(d, !!photoCameraId, d.id === photoCameraId) :
      d.typeId.startsWith('calibration.') ? calibrationModel(d) :
      new THREE.Group().add(new THREE.Mesh(new THREE.BoxGeometry(d.parameters.sizeX, d.parameters.sizeY, d.parameters.sizeZ), boxMaterial));
    object.userData.deviceId = d.id; object.visible = d.visible;
    const t = d.transform;
    object.position.set(t.x, t.y, t.z); object.rotation.set(t.rx * deg, t.ry * deg, t.rz * deg, 'ZYX');
    objects.set(d.id, object);
  }
  for (const d of devices) {
    let parent = d.parentId ? objects.get(d.parentId)! : root;
    if (d.parentId && d.attachmentFrameId !== 'base') parent = d.attachmentFrameId === 'flange' ? parent.userData.visual.flange : parent.userData.visual.tool;
    parent.add(objects.get(d.id)!);
  }
  if (runtime) applyRuntime(objects, runtime);
  root.updateMatrixWorld(true);
}

export class Viewport {
  private scene = new THREE.Scene();
  private camera = new THREE.PerspectiveCamera(42, 1, 1, 30000);
  private renderer: THREE.WebGLRenderer;
  private orbit: OrbitControls;
  private transform: TransformControls;
  private objects = new Map<string, THREE.Group>();
  private root = new THREE.Group();
  private grid: THREE.GridHelper;
  private selection: THREE.BoxHelper;
  private selected: string | null = null;
  private raycaster = new THREE.Raycaster();
  private pointerStart = [0, 0];
  private wasDragging = false;
  private frameCount = 0;
  private frameTime = performance.now();
  private workAxes = new THREE.AxesHelper(140);
  private trail = new THREE.Line(new THREE.BufferGeometry(), new THREE.LineBasicMaterial({color:0x69dfbb}));
  private trailPoints: THREE.Vector3[] = [];
  private trailSession = '';
  private trailRobot: string | null = null;

  constructor(private container: HTMLElement, private onSelect: (id: string | null) => void,
    private onTransform: (id: string, pose: Pose) => void) {
    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    this.renderer.setClearColor(0x121a25);
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    container.prepend(this.renderer.domElement);
    this.scene.add(this.root, new THREE.HemisphereLight(0xddeaff, 0x354051, 2.3));
    const light = new THREE.DirectionalLight(0xfff4e2, 3.3); light.position.set(1000, -800, 1700); this.scene.add(light);
    const rim = new THREE.DirectionalLight(0x9ec5ff, 1.5); rim.position.set(-800, 600, 1000); this.scene.add(rim);
    this.grid = new THREE.GridHelper(6000, 60, 0x3c4d5c, 0x243240); this.grid.rotation.x = Math.PI / 2;
    this.scene.add(this.grid);
    this.workAxes.matrixAutoUpdate = false; this.workAxes.visible = false;
    this.workAxes.add(frameLabel('W'));
    this.scene.add(this.workAxes, this.trail);
    this.scene.add(new THREE.AxesHelper(200));
    this.orbit = new OrbitControls(this.camera, this.renderer.domElement);
    this.orbit.enableDamping = true; this.orbit.dampingFactor = 0.09;
    this.orbit.maxDistance = 10000; this.orbit.minDistance = 120;
    this.home();
    this.transform = new TransformControls(this.camera, this.renderer.domElement);
    this.transform.setSize(0.85); this.scene.add(this.transform.getHelper());
    this.transform.addEventListener('dragging-changed', event => {
      this.orbit.enabled = !event.value;
      if (event.value) this.wasDragging = true;
    });
    this.transform.addEventListener('mouseUp', () => {
      const object = this.selected ? this.objects.get(this.selected) : null;
      if (object && this.selected) {
        const angles = new THREE.Euler().setFromQuaternion(object.quaternion, 'ZYX');
        this.onTransform(this.selected, { x: object.position.x, y: object.position.y, z: object.position.z,
          rx: angles.x / deg, ry: angles.y / deg, rz: angles.z / deg });
      }
    });
    this.selection = new THREE.BoxHelper(new THREE.Object3D(), 0x5ce0c5); this.selection.visible = false;
    this.scene.add(this.selection);
    this.renderer.domElement.addEventListener('pointerdown', e => {
      this.pointerStart = [e.clientX, e.clientY]; this.wasDragging = false;
    });
    this.renderer.domElement.addEventListener('pointerup', e => {
      if (this.wasDragging || this.transform.axis || e.button !== 0 || Math.hypot(e.clientX - this.pointerStart[0], e.clientY - this.pointerStart[1]) > 4) return;
      const rect = this.renderer.domElement.getBoundingClientRect();
      this.raycaster.setFromCamera(new THREE.Vector2((e.clientX - rect.left) / rect.width * 2 - 1, -(e.clientY - rect.top) / rect.height * 2 + 1), this.camera);
      const hits = this.raycaster.intersectObjects([...this.objects.values()], true);
      const hit = hits.find(h => {
        for (let current: THREE.Object3D | null = h.object; current; current = current.parent) if (!current.visible) return false;
        return h.object instanceof THREE.Mesh;
      });
      let object: THREE.Object3D | null = hit?.object ?? null;
      while (object && !object.userData.deviceId) object = object.parent;
      this.onSelect(object?.userData.deviceId ?? null);
    });
    new ResizeObserver(() => this.resize()).observe(container);
    this.resize();
    this.renderer.setAnimationLoop(() => {
      this.orbit.update(); if (this.selection.visible) this.selection.update();
      this.renderer.render(this.scene, this.camera);
      this.frameCount++;
      const elapsed = performance.now() - this.frameTime;
      if (elapsed >= 1000) {
        const fps = document.querySelector('#fps'); if (fps) fps.textContent = `${Math.round(this.frameCount * 1000 / elapsed)} FPS`;
        this.frameCount = 0; this.frameTime = performance.now();
      }
    });
  }

  resize() {
    const { clientWidth: w, clientHeight: h } = this.container;
    this.renderer.setSize(w, h); this.camera.aspect = w / Math.max(h, 1); this.camera.updateProjectionMatrix();
  }
  home() { this.camera.position.set(1600, -1900, 1400); this.orbit.target.set(0, 0, 220); this.orbit.update(); }
  setMode(mode: 'translate' | 'rotate') { this.transform.setMode(mode); }
  toggleGrid() { this.grid.visible = !this.grid.visible; return this.grid.visible; }
  focus() {
    const selected = this.selected ? this.objects.get(this.selected) : null;
    if (!selected) { this.home(); return; }
    const bounds = new THREE.Box3().setFromObject(selected);
    const center = bounds.getCenter(new THREE.Vector3());
    const distance = Math.max(450, bounds.getSize(new THREE.Vector3()).length() * 1.7);
    this.camera.position.copy(center).add(new THREE.Vector3(1, -1.2, 0.8).normalize().multiplyScalar(distance));
    this.orbit.target.copy(center); this.orbit.update();
  }
  setDevices(devices: Device[], runtime?: RuntimeSnapshot) {
    this.transform.detach();
    disposeGraph(this.root);
    this.root.clear(); this.objects.clear();
    populateGraph(this.root, this.objects, devices, runtime);
    if (runtime) this.updateRuntime(runtime);
    this.scene.updateMatrixWorld(true);
    this.select(this.selected);
  }
  updateRuntime(runtime: RuntimeSnapshot) {
    applyRuntime(this.objects, runtime);
    this.scene.updateMatrixWorld(true);
    this.transform.enabled = !runtime.programRunId && !runtime.robots.some(r => r.status === 'Running' || r.controlOwnerId);
    const robot = runtime.robots.find(r=>r.deviceId === this.selected);
    this.workAxes.visible = !!robot;
    if (robot) this.workAxes.matrix.fromArray(robot.workWorld.matrix);
    if (this.trailSession !== runtime.sessionId || this.trailRobot !== (robot?.deviceId ?? null)) {
      this.trailPoints = []; this.trailSession = runtime.sessionId; this.trailRobot = robot?.deviceId ?? null;
    }
    if (robot) {
      const point = new THREE.Vector3().fromArray(robot.tcpWorld.matrix,12);
      if (!this.trailPoints.length || point.distanceTo(this.trailPoints.at(-1)!) > .5) {
        this.trailPoints.push(point); if(this.trailPoints.length>1500) this.trailPoints.shift();
      }
    }
    this.trail.geometry.dispose(); this.trail.geometry = new THREE.BufferGeometry().setFromPoints(this.trailPoints);
  }
  aimCamera(id: string): Pose {
    const object = this.objects.get(id)!;
    this.scene.updateMatrixWorld(true);
    const bounds = new THREE.Box3();
    for (const candidate of this.objects.values()) if (candidate !== object && candidate.userData.visual) bounds.expandByObject(candidate);
    const target = bounds.isEmpty() ? new THREE.Vector3(0, 0, 80) : bounds.getCenter(new THREE.Vector3());
    const position = object.getWorldPosition(new THREE.Vector3());
    if (position.distanceTo(target) < 1) throw new Error('相机与目标中心重合，请先移动相机。');
    const optical = new THREE.PerspectiveCamera(); optical.position.copy(position); optical.lookAt(target); optical.updateMatrixWorld(true);
    const world = optical.matrixWorld.clone().multiply(new THREE.Matrix4().makeScale(1, -1, -1));
    const local = object.parent!.matrixWorld.clone().invert().multiply(world);
    const angles = new THREE.Euler().setFromRotationMatrix(local, 'ZYX');
    const p = new THREE.Vector3().setFromMatrixPosition(local);
    return { x: p.x, y: p.y, z: p.z, rx: angles.x / deg, ry: angles.y / deg, rz: angles.z / deg };
  }
  async capture(work: CaptureWork): Promise<Blob> {
    if (this.renderer.getContext().isContextLost()) throw new Error('WebGL 上下文已失效，请重启界面。');
    const frozen = work.snapshot;
    const p = frozen.camera.parameters;
    const scene = new THREE.Scene(); scene.background = new THREE.Color(0x252e3a);
    const graph = new THREE.Group(); populateGraph(graph, new Map(), frozen.project.devices, frozen.runtime, frozen.camera.id);
    scene.add(graph, new THREE.HemisphereLight(0xddeaff, 0x354051, 2.3));
    const light = new THREE.DirectionalLight(0xfff4e2, 3.3); light.position.set(1000, -800, 1700); scene.add(light);
    const rim = new THREE.DirectionalLight(0x9ec5ff, 1.5); rim.position.set(-800, 600, 1000); scene.add(rim);
    const floorMaterial = new THREE.MeshStandardMaterial({ color: 0x323b47, roughness: 1 });
    const floor = new THREE.Mesh(new THREE.PlaneGeometry(6000, 6000), floorMaterial); floor.position.z = -1; scene.add(floor);
    const camera = new THREE.PerspectiveCamera(); camera.matrixAutoUpdate = false;
    camera.matrix.fromArray(frozen.runtime.devices.find(d => d.deviceId === frozen.camera.id)!.world.matrix)
      .multiply(new THREE.Matrix4().makeScale(1, -1, -1));
    camera.projectionMatrix.makePerspective(-p.cx / p.fx, (p.width - p.cx) / p.fx, p.cy / p.fy, -(p.height - p.cy) / p.fy, 1, 30000);
    camera.projectionMatrixInverse.copy(camera.projectionMatrix).invert(); camera.updateMatrixWorld(true);
    const target = new THREE.WebGLRenderTarget(p.width, p.height, { samples: 4 }); target.texture.colorSpace = THREE.SRGBColorSpace;
    const previous = this.renderer.getRenderTarget();
    const pixels = new Uint8Array(p.width * p.height * 4);
    try {
      this.renderer.setRenderTarget(target); this.renderer.render(scene, camera);
      this.renderer.readRenderTargetPixels(target, 0, 0, p.width, p.height, pixels);
    } finally {
      this.renderer.setRenderTarget(previous); target.dispose(); disposeGraph(graph); floor.geometry.dispose(); floorMaterial.dispose();
    }
    const rgba = new Uint8ClampedArray(pixels.length);
    for (let y = 0; y < p.height; y++) rgba.set(pixels.subarray((p.height - 1 - y) * p.width * 4, (p.height - y) * p.width * 4), y * p.width * 4);
    const canvas = document.createElement('canvas'); canvas.width = p.width; canvas.height = p.height;
    canvas.getContext('2d')!.putImageData(new ImageData(rgba, p.width, p.height), 0, 0);
    return new Promise((resolve, reject) => canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error('PNG 编码失败。')), 'image/png'));
  }
  select(id: string | null) {
    this.selected = id;
    const object = id ? this.objects.get(id) : null;
    this.selection.visible = !!object && object.visible;
    if (object) { this.selection.setFromObject(object); this.transform.attach(object); }
    else this.transform.detach();
  }
}
