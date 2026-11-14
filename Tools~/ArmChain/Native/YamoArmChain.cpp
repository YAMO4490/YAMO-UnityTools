// Native Biped pose writer and explicit per-frame FK export. MAX files use stock controllers only.
#include <max.h>
#include <gup.h>
#include <iparamb2.h>
#include <maxscript/maxscript.h>
#include <maxscript/foundation/strings.h>
#include <maxscript/foundation/numbers.h>
#include <maxscript/maxwrapper/mxsobjects.h>
#include <CS/BipedApi.h>
#include <CS/Biped9Api.h>
#include <vector>
#include <array>
#include <string>
#include <fstream>
#include <algorithm>
#include <cmath>
#include <stdexcept>
#include <filesystem>
#include <istdplug.h>
#include <plugapi.h>
#include <Geom/euler.h>

static HINSTANCE instanceHandle;
static const wchar_t* suffixes[8]={L" L Clavicle",L" L UpperArm",L" L Forearm",L" L Hand",L" R Clavicle",L" R UpperArm",L" R Forearm",L" R Hand"};
using Row=std::array<float,56>;
static std::array<INode*,8> Nodes(INode* root){
    std::array<INode*,8> n{};std::wstring prefix=root->GetName();
    for(int i=0;i<8;i++){n[i]=GetCOREInterface()->GetINodeByName((prefix+suffixes[i]).c_str());if(!n[i])throw std::runtime_error("missing arm node");}return n;
}
static Point3 P(const Row&r,int bone){return Point3(r[bone*7],r[bone*7+1],r[bone*7+2]);}
static Quat Q(const Row&r,int bone){return Quat(r[bone*7+3],r[bone*7+4],r[bone*7+5],r[bone*7+6]);}
static Point3 Unit(Point3 p){float l=Length(p);return l>1e-9f?p/l:Point3(0,0,0);}
static Matrix3 Basis(Point3 x,Point3 z,Point3 pos){Matrix3 m(1);m.SetRow(0,x);m.SetRow(1,Unit(CrossProd(z,x)));m.SetRow(2,z);m.SetTrans(pos);return m;}
static Row Sample(const std::array<INode*,8>&n,TimeValue t){
    Row r{};for(int i=0;i<8;i++){Matrix3 m=n[i]->GetNodeTM(t);Quat q(m);q.Normalize();Point3 p=m.GetTrans();r[i*7]=p.x;r[i*7+1]=p.y;r[i*7+2]=p.z;r[i*7+3]=q.x;r[i*7+4]=q.y;r[i*7+5]=q.z;r[i*7+6]=q.w;}return r;
}
static Value* SampleBinary(Value**a,int count){
    try{if(count!=4&&count!=5)throw std::runtime_error("sample expects root path first last and optional tick offset");
        auto n=Nodes(a[0]->to_node());const wchar_t*path=a[1]->to_string();int first=a[2]->to_int(),last=a[3]->to_int();
        if(first<0||last<first||last-first>50000)throw std::runtime_error("invalid frame range");
        std::ofstream out(std::filesystem::path(path),std::ios::binary);int sz=last-first+1,cols=56;out.write((char*)&sz,4);out.write((char*)&cols,4);
        TimeValue offset=count==5?a[4]->to_int():0;for(int f=first;f<=last;f++){auto r=Sample(n,offset+f*GetTicksPerFrame());out.write((char*)r.data(),sizeof(Row));}
        if(!out)throw std::runtime_error("sample write failed");return Integer::intern(sz);
    }catch(const std::exception&e){std::string s=e.what();std::wstring ws(s.begin(),s.end());return new String(ws.c_str());}
}
static Value* ApplyBinary(Value**a,int count){
    IBipDriver9*d9=nullptr;bool started=false;
    try{if(count!=2&&count!=3)throw std::runtime_error("apply expects root path and optional first writable frame");
        INode*root=a[0]->to_node();auto n=Nodes(root);Control*ctrl=root->GetTMController();auto driver=GetBipDriverInterface(ctrl);d9=GetBipDriver9Interface(ctrl);if(!driver||!d9)throw std::runtime_error("not Biped 9");
        std::filesystem::path inputPath(a[1]->to_string());
        auto progress=[&](const char*phase,int frame){std::ofstream p(inputPath.wstring()+L".native-progress.txt");p<<phase<<" "<<frame;};
        std::ifstream in(inputPath,std::ios::binary);int sz=0,cols=0;in.read((char*)&sz,4);in.read((char*)&cols,4);if(sz<2||sz>50002||cols!=56)throw std::runtime_error("invalid pose file");std::vector<Row> target(sz);in.read((char*)target.data(),sz*sizeof(Row));if(!in)throw std::runtime_error("pose read failed");
        std::array<std::pair<int,int>,8> ids;for(int i=0;i<8;i++){int id,link;if(!driver->GetIdLink(n[i],id,link))throw std::runtime_error("Biped id failed");ids[i]={id,link};}
        std::array<float,2> upper,lower;for(int arm=0;arm<2;arm++){int b=arm*4;auto r=Sample(n,0);upper[arm]=Length(P(r,b+2)-P(r,b+1));lower[arm]=Length(P(r,b+3)-P(r,b+2));}
        // Capture parent frames before any key writes; no torso or leg keys are changed.
        std::vector<std::array<Matrix3,2>> parents(sz);std::vector<std::array<Point3,2>> shoulders(sz);
        for(int f=0;f<sz;f++){if(f%1000==0)progress("cache",f);for(int arm=0;arm<2;arm++){int b=arm*4;parents[f][arm]=n[b]->GetNodeTM(f*GetTicksPerFrame());shoulders[f][arm]=n[b+1]->GetNodeTM(f*GetTicksPerFrame()).GetTrans();}}
        std::array<Point3,2> lastZ{Point3(0,0,1),Point3(0,0,1)};
        std::array<std::array<Quat,3>,2> previousQ;for(int arm=0;arm<2;arm++)for(int k=0;k<3;k++){int b=arm*4+k+1;Matrix3 m=n[b]->GetNodeTM(0)*Inverse(n[b-1]->GetNodeTM(0));previousQ[arm][k]=Quat(m);previousQ[arm][k].Normalize();}
        int firstWritable=count==3?a[2]->to_int():1;if(firstWritable<0||firstWritable>2)throw std::runtime_error("first writable frame must be zero, one or two");for(int begin=firstWritable;begin<sz;begin+=sz){int end=sz;d9->StartSettingBipedKeys();started=true;
            for(int f=begin;f<end;f++){if(f%1000==0)progress("write",f);for(int arm=0;arm<2;arm++){
                int b=arm*4;Point3 S=shoulders[f][arm],E0=P(target[f],b+2),H0=P(target[f],b+3),S0=P(target[f],b+1);Point3 axis=Unit(H0-S);float dist=Length(H0-S),L=upper[arm],l=lower[arm];float reach=std::clamp(dist,std::abs(L-l)+1e-5f,L+l-1e-5f);Point3 H=S+axis*reach;float along=(L*L-l*l+reach*reach)/(2*reach),radius=std::sqrt(std::max(0.f,L*L-along*along));Point3 bend=E0-S0;bend-=axis*DotProd(bend,axis);bend=Length(bend)>1e-6f?Unit(bend):Unit(CrossProd(lastZ[arm],axis));Point3 E=S+axis*along+bend*radius;Point3 x=Unit(E-S),fore=Unit(H-E),z=-Unit(CrossProd(x,fore));if(Length(z)<.5f)z=Unit(lastZ[arm]-x*DotProd(lastZ[arm],x));lastZ[arm]=z;
                Matrix3 U=Basis(x,z,S),F=Basis(fore,z,E),Hand(1);Q(target[f],b+3).MakeMatrix(Hand);Hand.SetTrans(H);
                Matrix3 localU=U*Inverse(parents[f][arm]),localF=F*Inverse(U),localH=Hand*Inverse(F);Quat qu(localU),qf(localF),qh(localH);qu.Normalize();qf.Normalize();qh.Normalize();TimeValue t=f*GetTicksPerFrame();
                Quat qs[3]={qu,qf,qh};for(int k=0;k<3;k++){Quat prev=previousQ[arm][k];if(qs[k].x*prev.x+qs[k].y*prev.y+qs[k].z*prev.z+qs[k].w*prev.w<0)qs[k]=-qs[k];previousQ[arm][k]=qs[k];d9->SetBipedLocalRotation(t,qs[k],ids[b+k+1].first,ids[b+k+1].second);}
            }}progress("commit",sz);d9->StopSettingBipedKeys();started=false;
        }progress("complete",sz);return Integer::intern(sz-1);
    }catch(const std::exception&e){if(started&&d9)d9->StopSettingBipedKeys();std::string s=e.what();std::wstring ws(s.begin(),s.end());return new String(ws.c_str());}
}
static Value* CaptureSkeleton(Value**a,int count){
    try{if(count!=3)throw std::runtime_error("capture expects path first last");int first=a[1]->to_int(),last=a[2]->to_int(),frames=last-first+1,bones=GetCOREInterface()->GetSelNodeCount();if((bones<8||bones>2048)||first<0||frames<2||frames>50001)throw std::runtime_error("invalid Export_Biped selection or range");
        std::vector<INode*>ns(bones);for(int i=0;i<bones;i++)ns[i]=GetCOREInterface()->GetSelNode(i);
        std::ofstream out(std::filesystem::path(a[0]->to_string()),std::ios::binary);out.write((char*)&frames,4);out.write((char*)&bones,4);
        std::vector<int>parents(bones,-1);for(int i=0;i<bones;i++){std::wstring name=ns[i]->GetName();int len=(int)name.size();out.write((char*)&len,4);out.write((char*)name.data(),len*sizeof(wchar_t));for(int j=0;j<bones;j++)if(ns[i]->GetParentNode()==ns[j])parents[i]=j;out.write((char*)&parents[i],4);}
        std::vector<Quat>previous(bones,Quat(0.f,0.f,0.f,1.f));std::vector<Matrix3>world(bones);for(int f=0;f<frames;f++){TimeValue t=(first+f)*GetTicksPerFrame();for(int i=0;i<bones;i++)world[i]=ns[i]->GetNodeTM(t);for(int i=0;i<bones;i++){Matrix3 local=parents[i]>=0?world[i]*Inverse(world[parents[i]]):world[i];Point3 p=local.GetTrans();Quat q(local);q.Normalize();if((q.x*previous[i].x+q.y*previous[i].y+q.z*previous[i].z+q.w*previous[i].w)<0)q=-q;previous[i]=q;Point3 s(Length(local.GetRow(0)),Length(local.GetRow(1)),Length(local.GetRow(2)));if(std::max({std::abs(s.x-1),std::abs(s.y-1),std::abs(s.z-1)})>.001f)throw std::runtime_error("non-unit Biped transform requires affine scale support");float v[10]={p.x,p.y,p.z,q.x,q.y,q.z,q.w,s.x,s.y,s.z};out.write((char*)v,sizeof(v));}}
        if(!out)throw std::runtime_error("skeleton cache write failed");return Integer::intern(frames);
    }catch(const std::exception&e){std::string s=e.what();std::wstring ws(s.begin(),s.end());return new String(ws.c_str());}
}
static const int axisOrders[6][3]={{0,1,2},{0,2,1},{1,2,0},{1,0,2},{2,0,1},{2,1,0}};
static Quat ControllerEulerQuat(float*a,int order=EULERTYPE_XYZ){Matrix3 m(1);for(int i=0;i<3;i++)switch(axisOrders[order][i]){case 0:m.RotateX(a[i]);break;case 1:m.RotateY(a[i]);break;case 2:m.RotateZ(a[i]);break;}return Quat(m);}
static std::array<double,4> NormQuat(Quat q){double n=std::sqrt((double)q.x*q.x+(double)q.y*q.y+(double)q.z*q.z+(double)q.w*q.w);return{q.x/n,q.y/n,q.z/n,q.w/n};}
static void PreciseEuler(const Quat&v,float*a,int order=EULERTYPE_XYZ){
    // Use the controller's own matrix convention; SDK EulerToQuat is explicitly
    // worked around in Autodesk's eulrctrl.cpp sample and is not the oracle.
    QuatToEuler(v,a,order);auto target=NormQuat(v);
    auto value=[&](float*x){auto q=NormQuat(ControllerEulerQuat(x,order));double d=0;for(int k=0;k<4;k++)d+=q[k]*target[k];if(d<0)for(double&c:q)c=-c;return q;};
    for(int iteration=0;iteration<20;iteration++){
        auto q=value(a);double residual[4],error=0;for(int k=0;k<4;k++){residual[k]=target[k]-q[k];error+=residual[k]*residual[k];}if(error<1e-14)return;
        double jac[4][3];for(int axis=0;axis<3;axis++){float perturbed[3]={a[0],a[1],a[2]};perturbed[axis]+=.001f;auto p=value(perturbed);for(int k=0;k<4;k++)jac[k][axis]=(p[k]-q[k])/.001;}
        double m[3][4]{};for(int i=0;i<3;i++){for(int j=0;j<3;j++)for(int k=0;k<4;k++)m[i][j]+=jac[k][i]*jac[k][j];m[i][i]+=1e-8;for(int k=0;k<4;k++)m[i][3]+=jac[k][i]*residual[k];}
        for(int col=0;col<3;col++){int pivot=col;for(int r=col+1;r<3;r++)if(std::abs(m[r][col])>std::abs(m[pivot][col]))pivot=r;for(int c=col;c<4;c++)std::swap(m[col][c],m[pivot][c]);double den=m[col][col];for(int c=col;c<4;c++)m[col][c]/=den;for(int r=0;r<3;r++)if(r!=col){double k=m[r][col];for(int c=col;c<4;c++)m[r][c]-=k*m[col][c];}}
        bool improved=false;for(double scale:{1.,.5,.25,.125}){float trial[3];for(int axis=0;axis<3;axis++)trial[axis]=a[axis]+(float)(scale*std::clamp(m[axis][3],-.25,.25));auto next=value(trial);double e=0;for(int k=0;k<4;k++)e+=std::pow(next[k]-target[k],2);if(e<error){for(int k=0;k<3;k++)a[k]=trial[k];improved=true;break;}}if(!improved)break;
    }
    auto q=value(a);double error=0;for(int k=0;k<4;k++)error+=std::pow(q[k]-target[k],2);if(error>1e-10)throw std::runtime_error("Euler controller pose error exceeds tolerance");
}
static Value* BuildSkeleton(Value**a,int count){
    try{if(count<1||count>3)throw std::runtime_error("build expects cache path, Euler mode, and optional hand order");bool euler=count>=2&&a[1]->to_bool();int handOrder=count==3?a[2]->to_int():EULERTYPE_XYZ;if(handOrder<0||handOrder>5)throw std::runtime_error("invalid hand order");std::ifstream in(std::filesystem::path(a[0]->to_string()),std::ios::binary);int frames=0,bones=0;in.read((char*)&frames,4);in.read((char*)&bones,4);if(frames<2||frames>50001||(bones<8||bones>2048))throw std::runtime_error("invalid skeleton cache");
        std::vector<std::wstring>names(bones);std::vector<int>parents(bones);for(int i=0;i<bones;i++){int len;in.read((char*)&len,4);if(len<1||len>512)throw std::runtime_error("invalid bone name");names[i].resize(len);in.read((char*)names[i].data(),len*sizeof(wchar_t));in.read((char*)&parents[i],4);}
        std::vector<INode*>ns(bones);std::vector<Control*>positions(bones),rotations(bones),scales(bones);std::vector<IKeyControl*>pk(bones),rk(bones),sk(bones);std::vector<std::array<IKeyControl*,3>>ek(bones);std::vector<std::array<float,3>>previousEuler(bones);
        for(int i=0;i<bones;i++){Object*obj=(Object*)CreateInstance(HELPER_CLASS_ID,Class_ID(POINTHELP_CLASS_ID,0));if(!obj)throw std::runtime_error("point helper unavailable");ns[i]=GetCOREInterface()->CreateObjectNode(obj);ns[i]->SetName(names[i].c_str());ns[i]->SetBoneNodeOnOff(TRUE,0);ns[i]->SetBoneAutoAlign(FALSE);ns[i]->SetBoneFreezeLen(TRUE);Control*prs=NewDefaultMatrix3Controller();ns[i]->SetTMController(prs);positions[i]=(Control*)CreateInstance(CTRL_POSITION_CLASS_ID,Class_ID(LININTERP_POSITION_CLASS_ID,0));rotations[i]=(Control*)CreateInstance(CTRL_ROTATION_CLASS_ID,Class_ID(euler?EULER_CONTROL_CLASS_ID:LININTERP_ROTATION_CLASS_ID,0));scales[i]=(Control*)CreateInstance(CTRL_SCALE_CLASS_ID,Class_ID(LININTERP_SCALE_CLASS_ID,0));if(!positions[i]||!rotations[i]||!scales[i])throw std::runtime_error("linear controllers unavailable");prs->SetPositionController(positions[i]);prs->SetRotationController(rotations[i]);prs->SetScaleController(scales[i]);pk[i]=GetKeyControlInterface(positions[i]);rk[i]=GetKeyControlInterface(rotations[i]);sk[i]=GetKeyControlInterface(scales[i]);if(!pk[i]||(!euler&&!rk[i])||!sk[i])throw std::runtime_error("key controls unavailable");pk[i]->SetNumKeys(frames);if(euler){auto ec=(IEulerControl*)rotations[i]->GetInterface(I_EULERCTRL);if(!ec)throw std::runtime_error("Euler interface missing");ec->SetOrder(names[i].find(L" Hand")!=std::wstring::npos?handOrder:EULERTYPE_XYZ);for(int axis=0;axis<3;axis++){Control*c=(Control*)CreateInstance(CTRL_FLOAT_CLASS_ID,Class_ID(LININTERP_FLOAT_CLASS_ID,0));if(!rotations[i]->AssignController(c,axis))throw std::runtime_error("Euler axis assignment failed");ek[i][axis]=GetKeyControlInterface(c);if(!ek[i][axis])throw std::runtime_error("Euler key control missing");ek[i][axis]->SetNumKeys(frames);}}else rk[i]->SetNumKeys(frames);sk[i]->SetNumKeys(frames);}
        for(int i=0;i<bones;i++)if(parents[i]>=0)ns[parents[i]]->AttachChild(ns[i],0);
        for(int f=0;f<frames;f++)for(int i=0;i<bones;i++){float v[10];in.read((char*)v,sizeof(v));if(!in)throw std::runtime_error("incomplete skeleton cache");ILinPoint3Key p{};p.time=f*GetTicksPerFrame();p.val=Point3(v[0],v[1],v[2]);pk[i]->SetKey(f,&p);ILinRotKey r{};r.time=p.time;r.val=Quat(v[3],v[4],v[5],v[6]);if(euler){int order=names[i].find(L" Hand")!=std::wstring::npos?handOrder:EULERTYPE_XYZ;float angles[3];PreciseEuler(r.val,angles,order);float alternate[3]={angles[0]+PI,PI-angles[1],angles[2]+PI};Quat alt=ControllerEulerQuat(alternate,order);float dot=alt.x*r.val.x+alt.y*r.val.y+alt.z*r.val.z+alt.w*r.val.w;float scoreA=0,scoreB=0;for(int axis=0;axis<3;axis++){if(f>0){angles[axis]+=std::round((previousEuler[i][axis]-angles[axis])/TWOPI)*TWOPI;alternate[axis]+=std::round((previousEuler[i][axis]-alternate[axis])/TWOPI)*TWOPI;}scoreA+=std::pow(angles[axis]-previousEuler[i][axis],2);scoreB+=std::pow(alternate[axis]-previousEuler[i][axis],2);}bool useAlt=f>0&&std::abs(dot)>.99999f&&scoreB<scoreA;for(int axis=0;axis<3;axis++){ILinFloatKey fk{};fk.time=p.time;fk.val=useAlt?alternate[axis]:angles[axis];ek[i][axis]->SetKey(f,&fk);previousEuler[i][axis]=fk.val;}}else rk[i]->SetKey(f,&r);ILinScaleKey s{};s.time=p.time;s.val=ScaleValue(Point3(v[7],v[8],v[9]));sk[i]->SetKey(f,&s);}
        for(int i=0;i<bones;i++){pk[i]->SortKeys();if(euler){for(int axis=0;axis<3;axis++)ek[i][axis]->SortKeys();}else rk[i]->SortKeys();sk[i]->SortKeys();positions[i]->NotifyDependents(FOREVER,PART_ALL,REFMSG_CHANGE);rotations[i]->NotifyDependents(FOREVER,PART_ALL,REFMSG_CHANGE);scales[i]->NotifyDependents(FOREVER,PART_ALL,REFMSG_CHANGE);}GetCOREInterface()->SetAnimRange(Interval(0,(frames-1)*GetTicksPerFrame()));return Integer::intern(bones);
    }catch(const std::exception&e){std::string s=e.what();std::wstring ws(s.begin(),s.end());return new String(ws.c_str());}
}
static void Register(){static Primitive*p1=nullptr,*p2=nullptr,*p3=nullptr,*p4=nullptr;if(!p1)p1=new Primitive(_M("YamoArmChainSample"),SampleBinary);if(!p2)p2=new Primitive(_M("YamoArmChainApply"),ApplyBinary);if(!p3)p3=new Primitive(_M("YamoArmChainCapture"),CaptureSkeleton);if(!p4)p4=new Primitive(_M("YamoArmChainBuild"),BuildSkeleton);}
class StudyGUP:public GUP{public:DWORD Start()override{Register();return GUPRESULT_KEEP;}void Stop()override{}void DeleteThis()override{delete this;}};
class StudyDesc:public ClassDesc2{public:int IsPublic()override{return FALSE;}void*Create(BOOL)override{return new StudyGUP;}const MCHAR*ClassName()override{return _M("YamoArmChain2025");}const MCHAR*NonLocalizedClassName()override{return _M("YamoArmChain2025");}SClass_ID SuperClassID()override{return GUP_CLASS_ID;}Class_ID ClassID()override{return Class_ID(0x6a6d2301,0x1210a681);}const MCHAR*Category()override{return _M("YAMO Arm Chain");}const MCHAR*InternalName()override{return _M("YamoArmChain2025");}HINSTANCE HInstance()override{return instanceHandle;}};
static StudyDesc desc;
extern "C" __declspec(dllexport)const MCHAR*LibDescription(){return _M("YAMO arm-chain preservation; dedicated batch sessions only");}
extern "C" __declspec(dllexport)int LibNumberClasses(){return 1;}
extern "C" __declspec(dllexport)ClassDesc*LibClassDesc(int i){return i==0?&desc:nullptr;}
extern "C" __declspec(dllexport)ULONG LibVersion(){return VERSION_3DSMAX;}
extern "C" __declspec(dllexport)int LibInitialize(){Register();return TRUE;}
extern "C" __declspec(dllexport)int LibShutdown(){return TRUE;}
extern "C" __declspec(dllexport)ULONG CanAutoDefer(){return 0;}
BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){instanceHandle=h;DisableThreadLibraryCalls(h);}return TRUE;}
