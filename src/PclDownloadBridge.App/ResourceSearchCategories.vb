Imports PclDownloadBridge.Core

' Fixed PCL 5ef81e4 PageDownload{Mod,DataPack,ResourcePack,Shader}.xaml.
Friend NotInheritable Class ResourceSearchCategories
    Friend Shared Function Entries(kind As CommunityResourceKind) As KeyValuePair(Of String, String)()
        Select Case kind
            Case CommunityResourceKind.Mod
                Return { _
                    New KeyValuePair(Of String, String)("全部", "/"), _
                    New KeyValuePair(Of String, String)("世界元素", "406/worldgen"), _
                    New KeyValuePair(Of String, String)("生物群系", "407/"), _
                    New KeyValuePair(Of String, String)("维度", "410/"), _
                    New KeyValuePair(Of String, String)("矿物与资源", "408/"), _
                    New KeyValuePair(Of String, String)("天然结构", "409/"), _
                    New KeyValuePair(Of String, String)("科技", "412/technology"), _
                    New KeyValuePair(Of String, String)("管道与物流", "415/"), _
                    New KeyValuePair(Of String, String)("自动化", "4843/"), _
                    New KeyValuePair(Of String, String)("能源", "417/"), _
                    New KeyValuePair(Of String, String)("红石", "4558/"), _
                    New KeyValuePair(Of String, String)("食物与烹饪", "436/food"), _
                    New KeyValuePair(Of String, String)("农业", "416/"), _
                    New KeyValuePair(Of String, String)("游戏机制", "/game-mechanics"), _
                    New KeyValuePair(Of String, String)("运输", "414/transportation"), _
                    New KeyValuePair(Of String, String)("仓储", "420/storage"), _
                    New KeyValuePair(Of String, String)("魔法", "419/magic"), _
                    New KeyValuePair(Of String, String)("冒险", "422/adventure"), _
                    New KeyValuePair(Of String, String)("装饰", "424/decoration"), _
                    New KeyValuePair(Of String, String)("生物", "411/mobs"), _
                    New KeyValuePair(Of String, String)("实用", "5191/utility"), _
                    New KeyValuePair(Of String, String)("装备与工具", "434/equipment"), _
                    New KeyValuePair(Of String, String)("创造模式", "9026/"), _
                    New KeyValuePair(Of String, String)("性能优化", "6814/optimization"), _
                    New KeyValuePair(Of String, String)("信息显示", "423/"), _
                    New KeyValuePair(Of String, String)("服务器", "435/social"), _
                    New KeyValuePair(Of String, String)("支持库", "421/library")}
            Case CommunityResourceKind.DataPack
                Return { _
                    New KeyValuePair(Of String, String)("全部", "/"), _
                    New KeyValuePair(Of String, String)("世界元素", "/worldgen"), _
                    New KeyValuePair(Of String, String)("科技", "6951/technology"), _
                    New KeyValuePair(Of String, String)("游戏机制", "/game-mechanics"), _
                    New KeyValuePair(Of String, String)("运输", "/transportation"), _
                    New KeyValuePair(Of String, String)("仓储", "/storage"), _
                    New KeyValuePair(Of String, String)("魔法", "6952/magic"), _
                    New KeyValuePair(Of String, String)("冒险", "6948/adventure"), _
                    New KeyValuePair(Of String, String)("幻想", "6949/"), _
                    New KeyValuePair(Of String, String)("装饰", "/decoration"), _
                    New KeyValuePair(Of String, String)("生物", "/mobs"), _
                    New KeyValuePair(Of String, String)("实用", "6953/utility"), _
                    New KeyValuePair(Of String, String)("装备与工具", "/equipment"), _
                    New KeyValuePair(Of String, String)("性能优化", "/optimization"), _
                    New KeyValuePair(Of String, String)("服务器", "/social"), _
                    New KeyValuePair(Of String, String)("支持库", "6950/library"), _
                    New KeyValuePair(Of String, String)("Mod 相关", "6946/")}
            Case CommunityResourceKind.ResourcePack
                Return { _
                    New KeyValuePair(Of String, String)("全部", "/"), _
                    New KeyValuePair(Of String, String)("风格", Nothing), _
                    New KeyValuePair(Of String, String)("原版风", "403/vanilla-like"), _
                    New KeyValuePair(Of String, String)("写实风", "400/realistic"), _
                    New KeyValuePair(Of String, String)("现代风", "401/"), _
                    New KeyValuePair(Of String, String)("中世纪", "402/"), _
                    New KeyValuePair(Of String, String)("蒸汽朋克", "399/"), _
                    New KeyValuePair(Of String, String)("主题化", "/themed"), _
                    New KeyValuePair(Of String, String)("简洁", "/simplistic"), _
                    New KeyValuePair(Of String, String)("装饰", "/decoration"), _
                    New KeyValuePair(Of String, String)("战斗", "/combat"), _
                    New KeyValuePair(Of String, String)("实用", "/utility"), _
                    New KeyValuePair(Of String, String)("改良", "/tweaks"), _
                    New KeyValuePair(Of String, String)("鬼畜", "/cursed"), _
                    New KeyValuePair(Of String, String)("特性", Nothing), _
                    New KeyValuePair(Of String, String)("含实体", "/entities"), _
                    New KeyValuePair(Of String, String)("含声音", "/audio"), _
                    New KeyValuePair(Of String, String)("含字体", "5244/fonts"), _
                    New KeyValuePair(Of String, String)("含模型", "/models"), _
                    New KeyValuePair(Of String, String)("含语言", "/locale"), _
                    New KeyValuePair(Of String, String)("含 UI", "/gui"), _
                    New KeyValuePair(Of String, String)("核心着色器", "/core-shaders"), _
                    New KeyValuePair(Of String, String)("动态效果", "404/"), _
                    New KeyValuePair(Of String, String)("兼容 Mod", "4465/modded"), _
                    New KeyValuePair(Of String, String)("分辨率", Nothing), _
                    New KeyValuePair(Of String, String)("8x 或更低", "/8x-"), _
                    New KeyValuePair(Of String, String)("16x", "393/16x"), _
                    New KeyValuePair(Of String, String)("32x", "394/32x"), _
                    New KeyValuePair(Of String, String)("48x", "/48x"), _
                    New KeyValuePair(Of String, String)("64x", "395/64x"), _
                    New KeyValuePair(Of String, String)("128x", "396/128x"), _
                    New KeyValuePair(Of String, String)("256x", "397/256x"), _
                    New KeyValuePair(Of String, String)("512x 或更高", "398/512x+")}
            Case CommunityResourceKind.Shader
                Return { _
                    New KeyValuePair(Of String, String)("全部", "/"), _
                    New KeyValuePair(Of String, String)("风格", Nothing), _
                    New KeyValuePair(Of String, String)("原版风", "6555/vanilla-like"), _
                    New KeyValuePair(Of String, String)("幻想风", "6554/fantasy"), _
                    New KeyValuePair(Of String, String)("写实风", "6553/realistic"), _
                    New KeyValuePair(Of String, String)("半写实风", "/semi-realistic"), _
                    New KeyValuePair(Of String, String)("卡通风", "/cartoon"), _
                    New KeyValuePair(Of String, String)("特性", Nothing), _
                    New KeyValuePair(Of String, String)("彩色光照", "/colored-lighting"), _
                    New KeyValuePair(Of String, String)("路径追踪", "/path-tracing"), _
                    New KeyValuePair(Of String, String)("PBR", "/pbr"), _
                    New KeyValuePair(Of String, String)("反射", "/reflections"), _
                    New KeyValuePair(Of String, String)("性能负荷", Nothing), _
                    New KeyValuePair(Of String, String)("极低", "/potato"), _
                    New KeyValuePair(Of String, String)("低", "/low"), _
                    New KeyValuePair(Of String, String)("中", "/medium"), _
                    New KeyValuePair(Of String, String)("高", "/high"), _
                    New KeyValuePair(Of String, String)("加载器", Nothing), _
                    New KeyValuePair(Of String, String)("原版可用", "/vanilla"), _
                    New KeyValuePair(Of String, String)("Iris", "/iris"), _
                    New KeyValuePair(Of String, String)("OptiFine", "/optifine")}
            Case Else
                Return {New KeyValuePair(Of String, String)("全部", "/")}
        End Select
    End Function
End Class
