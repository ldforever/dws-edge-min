3DCfg说明：
一、线激光3D相关
	Persistence.cfg，线激光3D优先加载配置
二、静态3D
1、标定文件
1.1 奥比相机测体积需要，标定工具标定后获取，否则SDK无法正常启动
	camerasStitchedResult.yml		多相机拼接
	cameraToPlatformRT.yml		坐标系转换
	mesureArea.xml			测量区域
	RefPlaneRT.xml			参考平面
1.2 双目3D都是智能相机，可以无需上面标定这些配置

2、配置文件
	calVolumeParam.ini			静态3D配置
	kejieconfig.ini			单件分离相关配置

3、测体积时
	calVolumeParam.ini，目前只需修改两个参数
3.1 注意修改相机类型
3.2 注意修改测量高度


