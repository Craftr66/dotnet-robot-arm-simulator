export interface Pose { x: number; y: number; z: number; rx: number; ry: number; rz: number }
export interface Parameters {
  width: number; height: number; fx: number; fy: number; cx: number; cy: number;
  sizeX: number; sizeY: number; sizeZ: number;
}
export interface Device {
  id: string; typeId: string; name: string; deviceNumber: number; parentId: string | null;
  transform: Pose; parameters: Parameters; configurationRevision: number; visible: boolean;
  attachmentFrameId: 'base' | 'flange' | 'tool';
  robot: { modelId: string; jointPositions: number[]; toolTransform: Pose; workTransform: Pose; geometry: RobotGeometry } | null;
}
export interface RobotGeometry {
  baseHeight: number; shoulderHeight: number; upperArm: number; forearm: number; wrist1: number; wrist2: number; flangeLength: number;
  joint1Diameter: number; joint2Diameter: number; joint3Diameter: number; joint4Diameter: number; joint5Diameter: number; joint6Diameter: number;
}
export interface TeachPoint { id: string; number: number; robotId: string; name: string; joints: number[]; frame: string; target: Pose; dependencySignature: string }
export interface TeachPointView { point: TeachPoint; stale: boolean; reason: string | null }
export interface ProgramDefinition { version:number; name:string; workspace:Record<string,unknown> }
export interface ProgramValidation { valid:boolean; diagnostics:{blockId:string|null;code:string;message:string}[];blocks:{id:string;operation:string;depth:number}[] }
export interface ProgramState { sessionId:string;runId:string|null;status:'Idle'|'Running'|'Pausing'|'Paused'|'Completed'|'Failed'|'Cancelled';active:boolean;activeBlockId:string|null;activeOperation:string|null;executedBlocks:number;activeSeconds:number;errorCode:string|null;message:string|null;variables:Record<string,number>;lastCapture:{cameraId:string;captureId:string|null;success:boolean;frameId:string|null;imageUrl:string|null;errorCode:string|null;message:string|null}|null;logs:{sequence:number;at:string;blockId:string|null;level:string;message:string}[] }
export interface Project { schemaVersion: number; id: string; name: string; nextDeviceNumber: number; devices: Device[]; teachPoints: TeachPoint[]; nextTeachPointNumber: number; program:ProgramDefinition }
export interface Frame { pose: Pose; matrix: number[] }
export interface RobotState {
  deviceId: string; poseId: number; joints: number[]; status: 'Idle' | 'Running' | 'Completed' | 'Cancelled' | 'Failed';
  commandId: string | null; progress: number; jointMatrices: number[][];
  flangeBase: Frame; tcpBase: Frame; flangeWorld: Frame; tcpWorld: Frame;
  controlOwnerId: string | null; controlRemainingMilliseconds: number;
  workWorld: Frame; motionKind: string; stepIndex: number; stepCount: number; motionMessage: string | null; motionErrorCode: string | null; activeTeachPointId: string | null;
}
export interface RuntimeSnapshot {
  sessionId: string; projectRevision: number; robots: RobotState[]; devices: { deviceId: string; world: Frame }[];programRunId:string|null;
}
export interface JointDefinition { name: string; axis: string; minimum: number; maximum: number; maxVelocity: number }
export interface Snapshot { sessionId: string; revision: number; dirty: boolean; project: Project; hasSavedFile: boolean; runtime: RuntimeSnapshot; teaching: TeachPointView[] }
export interface RendererLease { rendererId: string; token: string; leaseSeconds: number }
export interface CaptureState { captureId: string; cameraId: string; status: 'Queued' | 'Rendering' | 'Completed' | 'Failed'; frameId: string | null; errorCode: string | null; message: string | null }
export interface CaptureWork { captureId: string; exposureAt: string; snapshot: { sessionId: string; sceneRevision: number; camera: Device; project: Project; runtime: RuntimeSnapshot } }
export interface FrameMetadata {
  frameId: string; captureId: string; cameraId: string; cameraName: string; sessionId: string; sceneRevision: number;
  cameraConfigurationRevision: number; exposureAt: string; completedAt: string; width: number; height: number;
  encoding: string; sha256: string; cameraWorld: Frame; intrinsics: Parameters; robots: RobotState[]; imageUrl: string;
}
export const typeNames: Record<string, string> = {
  'robot.generic6r': '六轴机械臂', 'camera.pinhole': '针孔相机', 'geometry.box': '长方体',
  'robot.generic3r': '三轴机械臂',
  'calibration.board': '九点标定板', 'calibration.marker': 'TCP 标定圆点',
};
export const kind = (typeId: string) => typeId.startsWith('robot') ? 'robot' : typeId.startsWith('camera') ? 'camera' : 'box';
