using System.Numerics;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // N. Sanity Beach past the beach (level 9, #137), Jungle Rollers (level 12),
    // Upstream (level 15), Boulder Dash (level 19), Whole Hog (level 30) and the Koala Kong cave
    // (level 33) have no sky: every mesh is ground,
    // banks, trunks, plants or cave rock cut along the 4:3 view (the retail
    // draw lists keep only what the 4:3 camera sees), and the 16:9 view saw
    // black past every cut. Open edges are continued outward in the plane of
    // their own face, with the face's texture, as a skirt. Edges of small
    // pieces (plants, leaves, props) are left alone: their continuation would
    // be a stretched leaf. The skirt is drawn behind all real scenery, in its
    // own depth order, so it only covers what nothing else does.
    //
    // Most open edges lie where the 16:9 view never looks or where real
    // scenery always stands in front, and a skirt on each of them (some 600
    // per mesh) costs more than the whole frame. Only the polygons listed
    // below get one: those whose skirt fills side-band pixels from at least
    // one camera path point (found offline by rendering every path point).
    const float NativeWideSkirtDistance = 3200;
    const int NativeWideSkirtMinPiece = 20;
    static readonly Dictionary<NativeWideSceneryKey, List<NativeWideRepair>> _nativeWideSkirts = [];

    // Jungle Rollers' skirted polygons per mesh, by header X and Z.
    static readonly Dictionary<(int X, int Z), int[]> NativeWideJungleSkirts = new()
    {
        // 0a_cW
        [(9900, 122966)] =
        [
            6, 13, 34, 42, 60, 73, 74, 85, 86, 87, 103, 109, 162, 163, 172, 181, 183, 184, 186, 194, 198, 200,
            201, 204, 206, 255, 256, 267, 273, 276, 277, 304, 347, 348, 671, 679, 680, 681, 686, 696, 735, 756,
            852, 869, 871, 958, 961
        ],
        // 0b_cW
        [(13228, 115223)] =
        [
            0, 1, 3, 4, 5, 50, 132, 141, 156, 162, 178, 233, 254, 261, 263, 551, 603, 640, 653, 696, 742, 768,
            776, 778, 781, 825, 867, 874, 1020, 1021, 1080, 1097, 1139, 1164, 1165, 1174, 1175
        ],
        // 0c_cW
        [(11093, 108123)] =
        [
            11, 13, 15, 19, 21, 178, 179, 313, 327, 331, 334, 339, 348, 381, 386, 392, 428, 430, 443, 445, 471,
            478, 482, 594, 618, 642, 643, 644, 653, 658, 661, 668, 818, 914, 1049, 1050, 1051, 1325, 1326,
            1569, 1592, 1618, 1620
        ],
        // 0d_cW
        [(11930, 101027)] =
        [
            4, 9, 18, 22, 24, 28, 111, 120, 265, 276, 286, 412, 414, 416, 451, 458, 469, 528, 546, 575, 576,
            580, 589, 590, 592, 604, 607, 620, 728, 953, 1150, 1154, 1325, 1326, 1354
        ],
        // 0e_cW
        [(9637, 94138)] =
        [
            50, 161, 166, 188, 191, 207, 252, 259, 292, 294, 296, 540, 583, 629, 727, 729, 737, 1012, 1014,
            1342, 1387, 1580, 1581, 1582, 1583, 1584, 1635
        ],
        // 0g_cW
        [(8249, 88318)] =
        [
            92, 106, 230, 241, 281, 295, 299, 303, 306, 308, 310, 530, 725, 727, 736, 741, 755, 1041
        ],
        // 0h_cW
        [(8382, 81540)] =
        [
            63, 68, 70, 312, 316, 324, 420, 421, 437, 627, 817, 818, 820, 823, 825, 838, 912, 916, 932, 940,
            970, 990, 1000, 1022, 1024, 1058, 1069, 1200, 1204, 1205, 1233, 1258, 1455, 1457, 1459, 1502, 1509,
            1523, 1540, 1559, 1563, 1564, 1577, 1584
        ],
        // 0i_cW
        [(3936, 74831)] =
        [
            6, 99, 112, 125, 143, 282, 284, 286, 287, 322, 330, 380, 423, 425, 449, 454, 482, 505, 507, 510,
            561, 584, 585, 587, 620, 621, 800, 804, 913, 928
        ],
        // 0j_cW
        [(400, 69109)] =
        [
            2, 4, 7, 9, 92, 98, 117, 123, 143, 145, 250, 278, 280, 316, 318, 542, 602, 668, 670, 854, 1064,
            1075, 1090
        ],
        // 0k_cW
        [(-1154, 61818)] =
        [
            292, 432, 439, 928, 1010, 1103, 1394, 1398, 1459, 1475, 1485, 1512, 1530, 1617, 1636, 1641, 1660,
            1930, 1931, 1934, 1960, 2283, 2284
        ],
        // 0l_cW
        [(-2291, 53944)] =
        [
            43, 50, 75, 83, 94, 609, 611, 656, 688, 704, 778, 838, 1012, 1017
        ],
        // 0m_cW
        [(5551, 47106)] =
        [
            45, 48, 56, 71, 101, 118, 130, 162, 225, 249, 258, 331, 340, 484, 487, 570, 571, 623, 635, 637,
            1260, 1273, 1286, 1297, 1316, 1512, 1533, 1553, 1555, 1557
        ],
        // 0n_cW
        [(5551, 41045)] =
        [
            10, 11, 36, 114, 136, 330, 332, 512, 519, 522, 528
        ],
        // 0o_cW
        [(8095, 34989)] =
        [
            9, 35, 68, 218, 222, 225, 238, 240, 241, 251, 276, 297, 402, 423, 431, 457, 481, 485, 496, 554,
            566, 568, 649, 650, 664, 665, 856, 869, 873, 881, 886, 887, 890, 971, 972, 991, 997
        ],
        // 0p_cW
        [(8519, 28933)] =
        [
            66, 82, 332, 340, 611, 614, 646, 712, 733, 743, 754, 758, 824, 832
        ],
        // 0q_cW
        [(11734, 23085)] =
        [
            6, 8, 10, 12, 14, 33, 103, 111, 117, 149, 155, 163, 258, 267, 268, 309, 324, 331, 355, 406, 519,
            540, 546, 550, 578, 604, 608, 619, 686, 696, 698, 887, 1026, 1289, 1558
        ],
        // 0r_cW
        [(10886, 15757)] =
        [
            12, 98, 112, 207, 209, 236, 278, 416, 418, 452, 460, 466, 589, 593, 666, 973, 992, 1034, 1035,
            1037, 1055, 1056, 1063, 1092, 1093, 1379, 1381, 1427
        ],
        // 0s_cW
        [(9614, 7572)] =
        [
            23, 25, 27, 29, 207, 214, 236, 255, 387, 408, 433, 473, 522, 550, 566, 572, 575, 630, 631, 654,
            715, 721, 723, 736, 750, 752, 754, 758, 759, 776, 791, 798, 802, 806, 810, 815, 816, 819, 825, 826,
            837, 839, 846
        ],
        // 0t_cW
        [(9826, -811)] =
        [
            2, 12, 14, 23, 25, 27, 41, 49, 66, 72, 116, 140, 162, 169, 368, 390, 396, 399, 426, 452, 465, 578,
            584, 615, 628, 668, 681, 683, 685, 721, 725, 1064, 1065, 1088, 1261, 1263, 1274, 1275, 1343, 1368,
            1404, 1488
        ],
        // 0u_cW
        [(8279, -9403)] =
        [
            57, 59, 61, 67, 99, 106, 146, 151, 227, 268, 279, 281, 296, 917, 949, 1025, 1350, 1354
        ],
    };

    // Upstream's skirted polygons. River water over black is not a hole
    // there: the 4:3 view shows the same river over black. Water over black
    // at the waterfalls and their top pools is. Selected offline (#161):
    // starting from every candidate skirt, a polygon is dropped when its
    // fills lie mostly in front of the scenery they border (drawn behind
    // that scenery, such a fill slides against it as the camera moves, e.g.
    // a riverbed piece continued up past the camera), then the rest rescanned.
    static readonly Dictionary<(int X, int Z), int[]> NativeWideUpstreamSkirts = new()
    {
        // 0d_fW
        [(8197, 100003)] =
        [
            30, 65, 73, 75, 166, 167, 190, 191, 257, 258, 260, 310, 335, 349, 376, 386, 387, 388, 397, 422,
            424, 425
        ],
        // 0e_fW
        [(8197, 91739)] =
        [
            1, 103, 105, 106, 110, 112, 144, 145, 187, 189, 191, 194, 197, 211, 227, 233, 263, 272, 287, 299,
            300, 302, 303, 308, 312, 670, 672, 719, 728, 733, 743, 945, 1004, 1015, 1081, 1089, 1122, 1133,
            1198, 1206, 1246, 1247, 1249, 1373, 1381, 1382, 1383, 1392, 1394, 1558, 1562, 1564, 1565, 1566
        ],
        // 0f_fW
        [(8198, 82687)] =
        [
            3, 27, 41, 97, 99, 104, 113, 221, 222, 229, 231, 236, 246, 248, 253, 262, 298, 309, 341, 347, 349,
            350, 362, 363, 364, 513, 514, 515, 519, 552, 578, 583, 589, 591, 592, 604, 605, 606, 777, 779, 836,
            846, 847, 863, 906, 914, 945, 946, 957, 1022, 1030, 1067, 1075, 1076, 1077, 1086, 1088, 1258, 1259,
            1260
        ],
        // 0g_fW
        [(8197, 73521)] =
        [
            5, 10, 59, 60, 77, 78, 84, 86, 93, 94, 356, 363, 365, 373, 375, 411, 660, 661, 762, 764, 765, 771,
            788, 790, 821, 827, 834, 845, 866, 867, 873, 885, 887, 888, 890, 891, 894, 1066, 1070, 1074, 1234,
            1355, 1416, 1422, 1429, 1432, 1435, 1483, 1540, 1546, 1553, 1556, 1594, 1613, 1628, 1649, 1824
        ],
        // 0h_fW
        [(8197, 64476)] =
        [
            10, 35, 36, 39, 51, 81, 161, 163, 170, 172, 173, 177, 233, 234, 291, 396, 397, 398, 401, 403, 424,
            425, 427, 428, 475, 502, 507, 513, 515, 516, 528, 530, 666, 676, 683, 700, 701, 712, 713, 714, 717,
            725, 726, 727, 744, 917, 927, 930, 1183, 1197, 1262, 1270, 1318, 1319, 1339, 1451, 1453, 1454,
            1456, 1458, 1484, 1491, 1492, 1493, 1503
        ],
        // 0i_fW
        [(8197, 55697)] =
        [
            16, 18, 26, 49, 79, 80, 86, 90, 92, 93, 94, 235, 341, 343, 344, 364, 369, 373, 378, 392, 452, 453,
            460, 462, 467, 474, 540, 541, 546, 548, 561, 584, 604, 606, 611, 632, 639, 643, 646, 657, 658, 659,
            995, 996, 1063, 1069, 1076, 1079, 1134, 1135, 1140, 1141, 1146, 1160, 1296, 1301, 1302, 1304, 1305,
            1370, 1378, 1409, 1410, 1411, 1412, 1418, 1442, 1482, 1483, 1496
        ],
        // 0j_fW
        [(8080, 46423)] =
        [
            29, 31, 43, 87, 112, 113, 120, 124, 140, 300, 301, 303, 305, 308, 310, 319, 340, 341, 352, 354,
            565, 566, 668, 676, 690, 695, 704, 744, 745, 763, 771, 778, 779, 916, 927, 930, 1100, 1101, 1112,
            1173, 1179, 1186, 1192, 1235, 1241, 1246, 1247, 1248, 1250, 1262, 1464, 1465
        ],
        // 0k_fW
        [(8197, 37530)] =
        [
            4, 6, 7, 36, 41, 54, 115, 116, 123, 125, 130, 138, 139, 171, 223, 224, 229, 289, 300, 391, 405,
            441, 452, 462, 477, 478, 485, 489, 491, 503, 504, 505, 692, 695, 701, 702, 705, 770, 778, 809, 811,
            868, 915, 916, 917, 924, 926, 1089, 1090, 1101, 1103
        ],
        // 0l_fW
        [(8197, 28810)] =
        [
            21, 23, 25, 28, 96, 102, 103, 108, 273, 276, 277, 281, 418, 420, 422, 425, 428, 429, 442, 453, 458,
            464, 484, 491, 498, 503, 504, 530, 531, 533, 534, 539, 894, 896, 915, 933, 1038, 1186, 1187, 1192,
            1193, 1196, 1261, 1269, 1300, 1301, 1304, 1310, 1311, 1314, 1379, 1387, 1418, 1427, 1428, 1575,
            1576, 1587, 1756, 1759, 1761
        ],
        // 0m_fW
        [(8198, 21246)] =
        [
            16, 17, 20, 36, 38, 66, 144, 146, 148, 149, 155, 171, 173, 180, 182, 285, 286, 333, 384, 385, 389,
            506, 507, 508, 511, 513, 514, 539, 541, 546, 547, 549, 605, 908, 909, 910, 920, 922, 958, 960, 961,
            983, 988, 989, 990, 1003, 1035, 1052, 1053, 1062, 1064, 1080, 1162
        ],
        // 0n_fW
        [(8197, 13652)] =
        [
            9, 91, 92, 96, 98, 116, 118, 120, 123, 126, 140, 221, 222, 224, 225, 230, 234, 519, 520, 535, 558,
            569, 606, 607, 616, 631, 896, 897, 966, 974, 1005, 1006, 1008, 1011, 1020, 1022, 1054, 1058, 1105,
            1106, 1107, 1114, 1116, 1278, 1279, 1290, 1292
        ],
        // 0o_fW
        [(8197, 4756)] =
        [
            16, 18, 55, 73, 85, 94, 95, 101, 103, 113, 115, 118, 119, 122, 126, 306, 468, 469, 490, 571, 573,
            578, 580, 592, 617, 621, 632, 674, 676, 777, 847, 862, 918, 924, 931, 934, 979, 981, 990, 991, 992,
            995, 1007, 1028, 1030, 1033, 1212, 1213, 1235, 1282, 1290, 1321, 1323, 1337, 1339, 1483, 1486
        ],
        // 0p_fW
        [(8197, -4074)] =
        [
            8, 10, 12, 15, 18, 32, 38, 43, 44, 71, 387, 388, 390, 416, 420, 425, 426, 437, 498, 499, 508, 513,
            523, 539, 551, 574, 585, 612, 621, 626, 636, 789, 790, 792, 801, 835, 840, 846, 848, 849, 862, 863,
            1052, 1053, 1119, 1127, 1158, 1159, 1162, 1163, 1169, 1172, 1175, 1238, 1246, 1249, 1277, 1292,
            1293, 1294, 1304, 1306, 1480, 1481
        ],
        // 0q_fW
        [(8197, -12905)] =
        [
            19, 46, 93, 94, 101, 103, 104, 108, 114, 140, 217, 219, 220, 224, 226, 239, 240, 242, 256, 279,
            291, 302, 329, 334, 338, 340, 342, 343, 357, 515, 517, 525, 529, 541, 542, 544, 545, 550, 554, 788,
            793, 794, 851, 859, 887, 891, 896, 897, 963, 971, 1002, 1017, 1018, 1019, 1020, 1029, 1031, 1201,
            1212, 1213, 1214, 1215, 1225, 1235
        ],
        // 0r_fW
        [(8197, -21736)] =
        [
            16, 18, 26, 88, 89, 93, 95, 96, 99, 370, 372, 373, 393, 398, 402, 404, 407, 479, 487, 489, 494,
            501, 509, 538, 540, 554, 589, 599, 625, 631, 637, 639, 640, 653, 654, 841, 883, 898, 953, 959, 966,
            969, 1034, 1035, 1040, 1053, 1056, 1193, 1198, 1199, 1265, 1273, 1304, 1305, 1331, 1332, 1333,
            1334, 1343, 1345
        ],
        // 0s_fW
        [(8197, -31233)] =
        [
            61, 64, 159, 161, 167, 178, 179, 220, 237, 287, 289, 290, 309, 320, 506, 514, 522, 586, 593, 631,
            632, 644, 647, 660, 662, 763, 793, 794
        ],
        // 1t_uW
        [(22042, -124755)] =
        [
            860
        ],
    };

    // Koala Kong's cave (level 33, one mesh): only the near floor and the
    // lava are continued; the rock is filled by the side walls of
    // NativeWideKoalaWalls (rock skirts became flat orange panels and streaks).
    static readonly Dictionary<(int X, int Z), int[]> NativeWideKoalaSkirts = new()
    {
        // 0__xW
        [(0, 0)] =
        [
            141, 267, 268
        ],
    };

    // Boulder Dash (level 19) is a canyon of rock walls with temple gates and
    // the mushroom path: all of it cut along the 4:3 view like Jungle Rollers.
    // Skirted polygons selected the same way, counting only the side-band
    // voids beside scenery (a pit seen at the 4:3 edge stays a pit).
    static readonly Dictionary<(int X, int Z), int[]> NativeWideBoulderDashSkirts = new()
    {
        // 0a_jW
        [(8811, 122898)] =
        [
            272, 386, 456, 476, 478, 597, 706, 730, 760, 775, 787, 789, 878, 879, 903, 951, 959, 966, 967, 999,
            1046, 1052, 1061, 1067, 1111, 1119, 1125, 1139, 1146, 1154, 1164, 1168, 1169, 1170, 1216, 1233,
            1268, 1275, 1286, 1309, 1394, 1404, 1512, 1514, 1563, 1596
        ],
        // 0b_jW
        [(10653, 114543)] =
        [
            2029, 2034, 2037, 2092, 2114, 2118, 2124, 2126, 2128, 2163, 2197, 2260, 2262, 2288, 2423, 2442,
            2453, 2454, 2523, 2550, 2588, 2642
        ],
        // 0c_jW
        [(11093, 106153)] =
        [
            151, 237, 362, 364, 391, 404, 406, 426, 502, 880, 891, 1798, 1822, 1828, 1835, 1865, 1965, 1977,
            1990, 1992, 2007, 2028, 2033, 2162, 2180, 2183, 2309, 2323, 2342, 2375, 2388, 2397, 2398, 2652,
            2656, 2661
        ],
        // 0d_jW
        [(9156, 97426)] =
        [
            178, 192, 232, 233, 238, 389, 560, 671, 862, 883, 971, 972, 1039, 1100, 1119, 1121, 1125, 1151,
            1152, 1153, 1324, 1380, 1382, 1396, 1417, 1419, 1423, 1439, 1441, 1449, 1450, 1451, 1456, 1460,
            1514, 1515, 1660
        ],
        // 0e_jW
        [(10232, 88296)] =
        [
            80, 208, 213, 249, 262, 285, 332, 345, 346, 421, 524, 542, 549, 562, 565, 567, 582, 592, 608, 844,
            872, 879, 881, 1086, 1197, 1210, 1211, 1291, 1295
        ],
        // 0f_jW
        [(9934, 79079)] =
        [
            316, 317, 344, 459, 564, 635, 636, 773, 794, 802, 803, 804, 818, 840, 868, 884, 900, 948, 952, 953,
            961, 1109, 1110, 1184, 1208, 1246, 1302, 1321, 1330, 1357, 1359, 1360, 1364
        ],
        // 0g_jW
        [(10067, 69372)] =
        [
            170, 174, 176, 180, 185, 223, 224, 798, 854, 863, 868, 877, 878, 884, 886, 956, 1131, 1166, 1171,
            1174, 1228, 1248, 1263, 1296, 1300, 1528, 1529, 1544, 1545
        ],
        // 0h_jW
        [(7602, 60174)] =
        [
            205, 214, 665, 772, 788, 798, 799, 800, 868, 884, 889, 891, 945
        ],
        // 0i_jW
        [(9119, 51470)] =
        [
            224, 280, 284, 300, 302, 350, 351, 352, 403, 417, 421, 471, 579, 600, 601, 697, 759, 795, 833, 849,
            859, 867, 871, 872, 893, 895, 1153, 1166, 1170
        ],
        // 0j_jW
        [(7564, 42535)] =
        [
            55, 99, 191, 193, 197, 199, 203, 205, 207, 209, 211, 231, 267, 321, 602, 682, 684, 902, 963, 965,
            1013, 1031, 1112
        ],
        // 0k_jW
        [(7814, 33836)] =
        [
            240, 245, 248, 667, 722, 724, 934, 964, 965, 1048, 1050, 1051, 1079, 1119, 1183, 1202, 1204, 1225,
            1232, 1233, 1234, 1249, 1256
        ],
        // 0l_jW
        [(7235, 24831)] =
        [
            216, 217, 356, 369, 461, 471, 486, 852, 893, 904, 905, 906, 911, 929, 1008, 1010, 1096, 1116, 1171,
            1175, 1197, 1199, 1212, 1213, 1420, 1421
        ],
        // 0m_jW
        [(7235, 15638)] =
        [
            239, 289, 294, 297, 377, 391, 421, 425, 491, 493, 689, 694, 809, 813, 826, 828, 832, 847, 849, 857,
            858, 859, 904, 932, 1022, 1038, 1039, 1067
        ],
        // 0n_jW
        [(8095, 6660)] =
        [
            50, 53, 61, 278, 292, 296, 346, 474, 475, 566, 608, 622, 658, 692, 895
        ],
        // 0o_jW
        [(8519, -2452)] =
        [
            340, 343, 352, 356, 363, 377, 446, 451, 457, 459, 463, 475, 504, 506, 508, 519, 520, 522, 624, 626,
            643, 646, 655, 659, 679, 681, 803, 804, 999, 1019, 1053, 1054, 1055, 1136, 1137, 1166, 1330, 1363,
            1364, 1402, 1411
        ],
        // 0p_jW
        [(10050, -11220)] =
        [
            504, 684, 721, 726, 729, 783, 803, 859, 863, 884, 886, 899, 900, 930, 1038, 1130, 1193, 1213, 1215,
            1236, 1244, 1245, 1246, 1342, 1343
        ],
        // 0q_jW
        [(9202, -19839)] =
        [
            227, 232, 313, 316, 330, 332, 334, 336, 340, 342, 344, 346, 348, 366, 402, 854, 859, 862, 913, 978,
            991, 1072, 1077
        ],
        // 0r_jW
        [(7930, -28500)] =
        [
            0, 8, 12, 28, 131, 304, 486, 491, 572, 593, 772, 827, 831, 845, 847, 851, 885, 886, 887, 1009,
            1125, 1156, 1162, 1164, 1198
        ],
        // 0s_jW
        [(8142, -37087)] =
        [
            134, 228, 253, 255, 265, 268, 304, 367, 380, 449, 471, 478, 517, 552, 556, 566, 726, 754, 761, 763,
            954, 1063, 1077, 1078, 1144, 1157, 1161
        ],
        // 0t_jW
        [(8279, -45679)] =
        [
            141, 187, 471, 474, 485, 582, 653, 679, 791, 793, 812, 819, 820, 821, 857, 886, 905, 916, 920,
            1060, 1083, 1089, 1109, 1122, 1131
        ],
        // 0u_jW
        [(8279, -55131)] =
        [
            647, 652, 663, 814, 1048, 1053, 1056, 1110, 1132, 1136, 1142, 1144, 1146, 1181, 1215, 1269, 1301,
            1381, 1434, 1463, 1522, 1565, 1577, 1582
        ],
        // 0v_jW
        [(8279, -65430)] =
        [
            300, 341, 352, 353, 392, 399, 482, 485, 495, 499, 505, 509, 513, 515, 517, 535, 1154, 1487
        ],
    };

    // Boulder Dash's three temple gates (a boulder waits on each) stand in a
    // wall across the whole canyon: a 45-degree ramp of three 400 rows, then
    // two 800 bands of wall, in 800 cells except one 1600 cell at each outer
    // end. The 4:3 cull removed or halved some of those end cells, mostly on
    // the right, and the 16:9 view saw black past the wall's corners. A half
    // end cell is completed; a missing one is the other end's cell mirrored
    // onto it; where both are missing (the third gate's top band), the next
    // inner cell is stretched over each half of the end. These cells keep their
    // true depth: a skirt of some nearer rock would otherwise win the corner.
    // Absolute wall Z and X of the left end's outer and inner edges and of the
    // right end's inner and outer edges, by mesh header X and Z.
    readonly record struct NativeWideGate(float Wall, float OuterLeft, float InnerLeft, float InnerRight,
        float OuterRight);
    static readonly Dictionary<(int X, int Z), NativeWideGate> NativeWideBoulderDashGates = new()
    {
        [(8279, -65430)] = new(-62806, 3591, 5199, 12399, 13999),  // 0v: the first gate
        [(9202, -19839)] = new(-19607, 3594, 5194, 12394, 14002),  // 0q: the second gate
        [(7564, 42535)] = new(43599, 3588, 5196, 12396, 14004),    // 0j: the third gate
    };

    static void NativeWideBoulderDashGateEnds(IMemory m, NativeWideWorld world, NativeWideGate gate,
        List<NativeWideRepair> output)
    {
        var origin = new Vector3((int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 4),
            (int)m.ReadU32(world.Header + 8));
        // Cells by side (0 left end, 1 right end, 2 left end's inner neighbour,
        // 3 right end's inner neighbour) and row (absolute Y range).
        var cells = new Dictionary<(int Side, float Low, float High), List<NativeWideRepair>>();
        float[] x0 = [gate.OuterLeft, gate.InnerRight, gate.InnerLeft, gate.InnerRight - 800];
        float[] x1 = [gate.InnerLeft, gate.OuterRight, gate.InnerLeft + 800, gate.InnerRight];
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            if (!TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            NativeWideClipVertex[] t = [ReadNativeWideLocal(m, world, a) with { U = u0, V = v0 },
                ReadNativeWideLocal(m, world, b) with { U = u1, V = v1 },
                ReadNativeWideLocal(m, world, c) with { U = u2, V = v2 }];
            float minX = t.Min(v => v.X) + origin.X, maxX = t.Max(v => v.X) + origin.X;
            float minZ = t.Min(v => v.Z) + origin.Z, maxZ = t.Max(v => v.Z) + origin.Z;
            if (minZ < gate.Wall - 1 || maxZ > gate.Wall + 1300 || t.Max(v => v.Y) - t.Min(v => v.Y) < 300) continue;
            for (int side = 0; side < 4; side++)
            {
                // Only triangles spanning the whole cell: no trims or details.
                if (minX < x0[side] - 2 || maxX > x1[side] + 2 || maxX - minX < x1[side] - x0[side] - 16) continue;
                var key = (side, t.Min(v => v.Y) + origin.Y, t.Max(v => v.Y) + origin.Y);
                if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = [];
                cell.Add(new(pi, t[0], t[1], t[2], true));
            }
        }
        // Copies a cell with its X range from0..from1 mapped onto to0..to1.
        void Map(List<NativeWideRepair> source, float from0, float from1, float to0, float to1)
        {
            float scale = (to1 - to0) / (from1 - from0);
            NativeWideClipVertex Move(NativeWideClipVertex v) =>
                v with { X = to0 - origin.X + (v.X + origin.X - from0) * scale };
            foreach (var r in source) output.Add(r with { A = Move(r.A), B = Move(r.B), C = Move(r.C) });
        }
        var whole = new Dictionary<(int Side, float Low, float High), List<NativeWideRepair>>();
        foreach (var (key, cell) in cells)
        {
            if (cell.Count == 1 && NativeWideOppositeCorner([cell[0].A, cell[0].B, cell[0].C],
                    out var p, out var q, out var corner))
            {
                cell.Add(new(cell[0].Polygon, p, q, corner, true));
                if (key.Side < 2) output.Add(cell[^1]);
            }
            if (cell.Count == 2) whole[key] = cell;
        }
        foreach (var (low, high) in whole.Keys.Select(k => (k.Low, k.High)).Distinct().ToList())
        {
            bool left = whole.TryGetValue((0, low, high), out var l);
            bool right = whole.TryGetValue((1, low, high), out var r);
            if (left && !right)
                Map(l!, gate.OuterLeft, gate.InnerLeft, gate.OuterRight, gate.InnerRight);
            else if (right && !left)
                Map(r!, gate.InnerRight, gate.OuterRight, gate.InnerLeft, gate.OuterLeft);
            else if (!left && !right)
            {
                float midLeft = (gate.OuterLeft + gate.InnerLeft) / 2, midRight = (gate.InnerRight + gate.OuterRight) / 2;
                if (whole.TryGetValue((2, low, high), out var nl))
                {
                    Map(nl, gate.InnerLeft, gate.InnerLeft + 800, gate.OuterLeft, midLeft);
                    Map(nl, gate.InnerLeft, gate.InnerLeft + 800, midLeft, gate.InnerLeft);
                }
                if (whole.TryGetValue((3, low, high), out var nr))
                {
                    Map(nr, gate.InnerRight - 800, gate.InnerRight, gate.InnerRight, midRight);
                    Map(nr, gate.InnerRight - 800, gate.InnerRight, midRight, gate.OuterRight);
                }
            }
        }
    }

    // The secret path ends in a room (0c) wider than the corridor leading to
    // it: 45-degree walls join the room's side walls to the corridor's at both
    // ends. The 4:3 cull dropped the two near ones (one triangle left), and
    // the 16:9 view saw the jungle outside through the gaps (#163). Each is
    // the opposite far corner's wall turned half a turn about the room's
    // centre, exactly where and how the surviving triangle lies. True depth.
    static readonly NativeWideCopies NativeWideBoulderDashEndRoom = new(
    [
        new(new(9997, -3194, 103593), new(10397, 1206, 103993), Vector3.Zero, MirrorX: 8797, MirrorZ: 105193),
        new(new(7197, -3194, 103593), new(7597, 1206, 103993), Vector3.Zero, MirrorX: 8797, MirrorZ: 105193),
    ], []);

    static void NativeWideBoulderDashEndWalls(IMemory m, NativeWideWorld world, List<NativeWideRepair> output)
    {
        // Copies landing on a retail triangle (the surviving one) are left out,
        // and so are the floor corners the boxes also hold.
        static (Vector3, Vector3, Vector3) Key(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3[] s = [a, b, c];
            Array.Sort(s, (p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y != q.Y ? p.Y.CompareTo(q.Y) : p.Z.CompareTo(q.Z));
            return (s[0], s[1], s[2]);
        }
        var retail = new HashSet<(Vector3, Vector3, Vector3)>();
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            retail.Add(Key(Position(ReadNativeWideLocal(m, world, a)), Position(ReadNativeWideLocal(m, world, b)),
                Position(ReadNativeWideLocal(m, world, c))));
        }
        foreach (var r in NativeWideCopyRepairs(m, world, NativeWideBoulderDashEndRoom))
        {
            if ((r.A.Y == r.B.Y && r.B.Y == r.C.Y) || retail.Contains(Key(Position(r.A), Position(r.B), Position(r.C))))
                continue;
            output.Add(r with { Boundary = true });
        }
    }

    // Whole Hog (level 30, #139): the village and jungle the hog rides through,
    // huts, banks and ground all cut along the 4:3 view like Jungle Rollers.
    // Skirted polygons selected the same way, beside-scenery voids only. Its
    // leaves and roof fringes are cut-out textures without the semi-trans
    // flag: polygons whose texture is 5% or more transparent were never
    // candidates (a stretched leaf is a striped see-through sheet).
    static readonly Dictionary<(int X, int Z), int[]> NativeWideWholeHogSkirts = new()
    {
        // 0d_uW
        [(170, 101027)] =
        [
            234, 243, 389, 401, 437, 456, 465, 471, 553, 558, 559, 568, 592, 638, 639, 641, 643, 644, 645, 680,
            682, 683
        ],
        // 0e_uW
        [(1438, 94138)] =
        [
            53, 68, 75, 90, 92, 101, 144, 466, 543, 547, 553, 558, 632, 735, 740, 749, 760, 796, 824, 834
        ],
        // 0g_uW
        [(2500, 88318)] =
        [
            13, 112, 123, 128, 205, 207, 226, 420, 465, 495, 503, 522, 523, 667, 668, 888, 938, 953, 972, 1047,
            1171, 1176, 1232, 1262, 1340, 1342, 1378, 1388, 1412, 1413, 1451, 1463
        ],
        // 0h_uW
        [(7077, 82440)] =
        [
            17, 71, 80, 120, 124, 180, 193, 235, 577, 684, 703, 1004, 1015, 1025, 1067, 1071, 1483, 1530, 1661,
            1663, 1665
        ],
        // 0i_uW
        [(6508, 76231)] =
        [
            98, 172, 174, 176, 218, 233, 271, 313, 388, 457, 462, 497, 609, 613, 617, 623, 625, 631, 638, 643,
            780, 835, 874, 983, 992, 994, 1003, 1013, 1019, 1021, 1025, 1029, 1114, 1195, 1197, 1199, 1201,
            1306, 1307, 1324
        ],
        // 0j_uW
        [(2834, 69609)] =
        [
            110, 122, 312, 335, 654, 688, 809, 851, 889, 891, 908, 938, 988, 990, 1006, 1077, 1079, 1084, 1198,
            1284, 1286, 1287, 1307, 1343, 1450, 1579, 1581, 1583, 1595, 1597
        ],
        // 0k_uW
        [(2585, 61557)] =
        [
            17, 153, 376, 380, 410, 453, 455, 511, 596, 788, 823, 979, 991, 1127, 1129, 1139, 1143, 1159, 1160,
            1202, 1296, 1474, 1495, 1536, 1640, 1642, 1648, 1649, 1781, 1839, 1956, 2065, 2068
        ],
        // 0l_uW
        [(5671, 53197)] =
        [
            187, 322, 324, 325, 335, 367, 432, 452, 500, 699, 849, 986, 1116, 1127, 1134, 1276, 1280, 1313,
            1332, 1404, 1427, 1476, 1526, 1616, 1625, 1626, 1630, 1641, 1904, 1951, 1952, 2085, 2087, 2089
        ],
        // 0m_uW
        [(9227, 46501)] =
        [
            212, 221, 233, 235, 239, 367, 369, 370, 372, 374, 382, 384, 391, 392, 396, 461, 469, 483, 651, 741,
            751, 816, 819, 1060, 1075
        ],
        // 0n_uW
        [(11066, 41045)] =
        [
            18, 189, 190, 206, 236, 251, 365, 369, 384, 389, 395, 398, 417, 419, 480, 528, 543, 574, 576, 588,
            600, 606, 717, 728, 739, 747, 748, 749, 758, 772, 773, 774, 775, 778, 805, 831, 844, 854, 855, 986,
            1000, 1060
        ],
        // 0o_uW
        [(12346, 34989)] =
        [
            248, 384, 385, 391, 393, 394, 415, 422, 435, 487, 507, 552, 553, 679, 815, 939, 947, 954, 1116,
            1203, 1219, 1264, 1269, 1310, 1397, 1408, 1619, 1666, 1677, 1724, 1863, 1865, 1867, 1902
        ],
        // 0p_uW
        [(12080, 28933)] =
        [
            61, 85, 136, 153, 164, 169, 170, 376, 414, 418, 536, 538, 563, 588, 912, 953, 1081, 1091, 1095
        ],
        // 0q_uW
        [(11504, 23085)] =
        [
            20, 33, 42, 160, 171, 173, 175, 179, 242, 275, 281, 364, 425, 504, 575, 576, 666, 711, 725, 756,
            829, 951, 954, 1098, 1102, 1114, 1116, 1401, 1559
        ],
        // 0r_uW
        [(8747, 15757)] =
        [
            498, 511, 694, 710, 721, 843, 862, 884, 948, 964, 1088, 1115, 1154, 1156, 1172, 1236, 1245, 1259,
            1352, 1435, 1437, 1439, 1535, 1560
        ],
        // 0s_uW
        [(9570, 7572)] =
        [
            4, 8, 10, 31, 32, 125, 172, 188, 248, 516, 561, 592, 600, 623, 720, 764, 766, 795, 968, 984, 1047,
            1072, 1076, 1109, 1126, 1153, 1155, 1166, 1181, 1272, 1274, 1275, 1276, 1282, 1288, 1290, 1292,
            1305, 1373, 1470, 1576, 1632, 1790, 1816, 1854, 1866
        ],
        // 0t_uW
        [(13715, -811)] =
        [
            25, 37, 51, 57, 85, 172, 173, 297, 354, 417, 454, 458, 460, 462, 580, 631, 705, 910, 935, 948,
            1294, 1463, 1473, 1477, 1611, 1647, 1767, 1769, 1771
        ],
        // 1b_uW
        [(14308, -8721)] =
        [
            2, 157, 513, 666, 668, 674, 731, 763, 769, 796, 845, 909, 988, 1051, 1057, 1058, 1094, 1172, 1273,
            1369, 1371, 1383, 1412, 1482, 1602, 1605, 1750, 1752, 1754, 1766, 1768, 1882, 1886
        ],
        // 1c_uW
        [(10167, -15820)] =
        [
            259, 266, 300, 446, 448, 541, 549, 610, 829, 955, 1060, 1062, 1065, 1118, 1128, 1140, 1142, 1158,
            1179, 1191, 1195, 1200, 1265, 1336, 1338, 1340
        ],
        // 1d_uW
        [(5536, -22916)] =
        [
            245, 267, 269, 697, 698, 778, 780, 818, 1037, 1039, 1096
        ],
        // 1e_uW
        [(7082, -29805)] =
        [
            13, 84, 124, 135, 140, 163, 205, 208, 533, 542, 546, 585, 605, 658, 707, 752, 899, 904, 1105, 1118,
            1137, 1145, 1213, 1350, 1355, 1517, 1519, 1563, 1564, 1570, 1596, 1634, 1646
        ],
        // 1g_uW
        [(8376, -35625)] =
        [
            0, 366, 474, 493, 620, 744, 746, 1114, 1172, 1219, 1348, 1350, 1352
        ],
        // 1h_uW
        [(13953, -41503)] =
        [
            182, 183, 192, 197, 212, 308, 326, 327, 329, 346, 374, 448, 567, 611, 613, 775, 779, 783, 784, 789,
            797, 804, 809, 829, 830, 831, 869, 884, 889, 934, 935, 941, 942, 978, 995, 997, 1006, 1016, 1024,
            1025, 1029, 1120, 1197, 1201, 1203, 1205, 1213, 1224, 1253, 1363, 1367, 1371, 1372
        ],
        // 1i_uW
        [(13260, -47712)] =
        [
            83, 292, 370, 401, 453, 529, 597, 648, 690, 747, 861, 898, 1146, 1148, 1150
        ],
        // 1j_uW
        [(8782, -54334)] =
        [
            148, 293, 346, 350, 405, 442, 548, 558, 583, 646, 647, 784, 968, 1076, 1078, 1081, 1132, 1149,
            1151, 1167, 1170, 1197, 1239, 1302, 1304, 1420, 1422, 1436, 1455, 1465, 1505, 1617
        ],
        // 1k_uW
        [(8479, -62387)] =
        [
            15, 34, 181, 256, 536, 549, 587, 607, 611, 721, 766, 795, 840, 844, 906, 1348, 1405, 1420, 1439,
            1448, 1520, 1665, 1670, 1848, 1862, 1918, 1927, 1934, 1953, 1954, 2006, 2013, 2017, 2169, 2170,
            2228, 2265, 2266, 2346, 2348, 2350
        ],
        // 1l_uW
        [(12240, -70746)] =
        [
            131, 260, 274, 289, 306, 307, 316, 321, 335, 361, 388, 391, 397, 409, 500, 512, 634, 642, 728, 745,
            795, 796, 808, 909, 951, 1158, 1160, 1345, 1365, 1452
        ],
        // 1m_uW
        [(16574, -77442)] =
        [
            12, 128, 130, 355, 359, 373, 376, 402, 550, 585, 614, 653, 659, 802, 842, 869, 881, 1032, 1034,
            1069
        ],
        // 1n_uW
        [(18814, -82898)] =
        [
            6, 46, 169, 184, 196, 232, 247, 353, 560, 702, 790, 899, 926, 927, 933, 934, 947, 954, 960, 978,
            1085, 1157, 1243, 1309, 1318, 1347, 1429, 1438, 1440, 1481
        ],
        // 1o_uW
        [(20374, -88954)] =
        [
            45, 55, 68, 138, 149, 154, 423, 427, 546, 548, 627, 789, 790, 813, 1071, 1080, 1242, 1252, 1262,
            1266
        ],
        // 1p_uW
        [(20050, -95010)] =
        [
            104, 126, 170, 179, 181, 185, 231, 256, 257, 263, 264, 345, 400, 538, 539, 753, 806, 846, 883, 944,
            1065, 1068, 1199, 1201, 1203, 1215, 1217, 1337, 1341
        ],
        // 1q_uW
        [(19348, -100858)] =
        [
            12, 13, 69, 70, 71, 81, 132, 142, 145, 146, 155, 174, 206, 503, 779, 811, 825, 961, 1005, 1101,
            1299, 1307, 1320, 1322
        ],
        // 1r_uW
        [(15989, -108186)] =
        [
            1, 16, 17, 21, 72, 88, 90, 92, 94, 178, 247, 263, 297, 310, 348, 387, 406, 407, 411, 538, 571, 582,
            702, 704, 732, 810, 858, 859, 861, 877, 1069, 1159, 1161, 1163, 1172, 1191, 1192, 1249, 1305, 1307,
            1308, 1309, 1321, 1323, 1325, 1328, 1344, 1383, 1444, 1602, 1612, 1616, 1629
        ],
        // 1s_uW
        [(16992, -116371)] =
        [
            181, 187, 189, 310, 319, 488, 490, 492, 619, 799, 931, 1060, 1067, 1428, 1628, 1688, 1735, 1870,
            1872, 1874
        ],
        // 1t_uW
        [(22042, -124755)] =
        [
            860
        ],
    };

    // Upstream's fallen trunk beside the second waterfall (0h_fW) ends in a
    // face at Z 61196 whose far corners (X 12797) are shaded black. The 4:3
    // view never reaches them; in 16:9 that face and its skirts showed as a
    // black wedge between the trunk and the bark below, widening as the
    // camera backs away. The far corners take the shading of the corners
    // across the face, in the mesh itself, so the 4:3 view, the side bands
    // and the skirts built from it all agree.
    static readonly (int Vertex, int From)[] NativeWideUpstreamTrunkEndLight = [(879, 871), (880, 871), (886, 871)];

    static bool NativeWideUpstreamTrunkMesh(IMemory m, NativeWideWorld world) =>
        world.PolyCount == 1780 && world.VertexCount == 2033
        && (int)m.ReadU32(world.Header) == 8197 && (int)m.ReadU32(world.Header + 8) == 64476;

    // The trunk's upper half face (713, 714) cut both the trunk's underside
    // (cells 708/709, a 32-texel tile repeated) and the bark strip below it
    // (cells 1450-1459, a 64-texel tile repeated) square, which read as a
    // notch in the bark. In the side bands that half face is not drawn, and
    // both rows go on: their last cell copied three more times, each top
    // corner moved on by the last top edge and each bottom corner by the last
    // bottom edge, drawn behind the scenery.
    static readonly int[] NativeWideUpstreamTrunkHidden = [713, 714, 724, 725];
    // Polygons of a row's last cell, its top edge (start, end) and bottom edge (start, end).
    static readonly (int[] Cell, int Top, int TopEnd, int Bottom, int BottomEnd)[] NativeWideUpstreamTrunkRows =
    [
        ([708, 709], 870, 871, 872, 873),
        ([1458, 1459], 1732, 1734, 1733, 1735),
    ];

    static int[]? NativeWideHiddenSides(IMemory m, NativeWideWorld world) =>
        m.ReadU32(Catalogs.Catalog.LevelIdAddr) == 15 && NativeWideUpstreamTrunkMesh(m, world)
            ? NativeWideUpstreamTrunkHidden : null;

    static void NativeWideUpstreamTrunkRowRepairs(IMemory m, NativeWideWorld world, List<NativeWideRepair> output)
    {
        foreach (var row in NativeWideUpstreamTrunkRows)
        {
            Vector3 topStep = Position(ReadNativeWideLocal(m, world, row.TopEnd)) - Position(ReadNativeWideLocal(m, world, row.Top));
            Vector3 bottomStep = Position(ReadNativeWideLocal(m, world, row.BottomEnd))
                - Position(ReadNativeWideLocal(m, world, row.Bottom));
            foreach (int pi in row.Cell)
            {
                uint poly = world.Polygons + (uint)pi * 8;
                NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
                if (!TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                    out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
                NativeWideClipVertex Corner(int index, short u, short v, int k)
                {
                    var p = ReadNativeWideLocal(m, world, index);
                    var step = index == row.Top || index == row.TopEnd ? topStep : bottomStep;
                    return p with { X = p.X + step.X * k, Y = p.Y + step.Y * k, Z = p.Z + step.Z * k, U = u, V = v };
                }
                for (int k = 1; k <= 3; k++)
                    output.Add(new(pi, Corner(a, u0, v0, k), Corner(b, u1, v1, k), Corner(c, u2, v2, k)));
            }
        }
        // Where the two rows' copies meet, single pixels were left open: the
        // hidden half face backs them, ranked behind every other fill.
        foreach (int pi in NativeWideUpstreamTrunkHidden)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            if (!TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            output.Add(new(pi, ReadNativeWideLocal(m, world, a) with { U = u0, V = v0 },
                ReadNativeWideLocal(m, world, b) with { U = u1, V = v1 },
                ReadNativeWideLocal(m, world, c) with { U = u2, V = v2 }, Rank: NativeWideRankMax));
        }
    }

    static void NativeWideUpstreamTrunkEnd(IMemory m, NativeWideWorld world)
    {
        if (!NativeWideUpstreamTrunkMesh(m, world))
            return;
        foreach (var (vertex, from) in NativeWideUpstreamTrunkEndLight)
        {
            uint address = world.Vertices + (uint)vertex * 8;
            uint word = m.ReadU32(address);
            uint rgb = m.ReadU32(world.Vertices + (uint)from * 8) & 0xFFFFFFu;
            if ((word & 0xFFFFFFu) != rgb) m.WriteU32(address, (word & 0xFF000000u) | rgb);
        }
    }

    static IReadOnlyList<NativeWideRepair> NativeWideSceneRepairs(IMemory m, NativeWideWorld world)
    {
        var repairs = NativeWideAssetRepairs(m, world);
        uint level = m.ReadU32(Catalogs.Catalog.LevelIdAddr);
        if (level == 15) NativeWideUpstreamTrunkEnd(m, world);
        var skirts = level switch
        {
            9 => NativeWideBeachSkirts, 12 => NativeWideJungleSkirts, 15 => NativeWideUpstreamSkirts,
            19 => NativeWideBoulderDashSkirts, 30 => NativeWideWholeHogSkirts, 33 => NativeWideKoalaSkirts, _ => null
        };
        if (skirts == null && level != 18) return repairs;
        var key = new NativeWideSceneryKey(level, m.ReadU32(world.Header), m.ReadU32(world.Header + 4),
            m.ReadU32(world.Header + 8), world.PolyCount, world.VertexCount);
        if (_nativeWideSkirts.TryGetValue(key, out var cached)) return cached;
        // Jungle Rollers' hand-made opening bank and trunk strips, and N. Sanity
        // Beach's sky, sand, terraces and pillar, keep their true depth. Upstream's opening bank strips overlap the retail bank
        // they continue, so they stay behind the scenery like the skirts.
        var all = new List<NativeWideRepair>(repairs.Count);
        foreach (var repair in repairs) all.Add(repair with { Boundary = level is 9 or 12 });
        int authored = all.Count;
        if (level == 19 && NativeWideBoulderDashGates.TryGetValue(((int)m.ReadU32(world.Header),
                (int)m.ReadU32(world.Header + 8)), out var gate))
            NativeWideBoulderDashGateEnds(m, world, gate, all);
        if (level == 19 && (int)m.ReadU32(world.Header) == 11093 && (int)m.ReadU32(world.Header + 8) == 106153)
            NativeWideBoulderDashEndWalls(m, world, all);
        if (skirts != null && skirts.TryGetValue(((int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 8)),
                out var skirted))
            NativeWideSkirtRepairs(m, world, [.. skirted], all, river: level == 15, planar: level == 33);
        if (level == 15 && NativeWideUpstreamTrunkMesh(m, world)) NativeWideUpstreamTrunkRowRepairs(m, world, all);
        if (level == 33) NativeWideKoalaWalls(m, world, all);
        if (level == 18) NativeWideGateRepairs(m, world, all);
        PaceLog($"native-wide level={level} skirt repairs={all.Count - authored}");
        return _nativeWideSkirts[key] = all;
    }

    // The Koala Kong arena ends at X = -4000 and X = 4000 (lava, bank and back
    // wall all stop there), but beside the two rock pillars at its ends
    // nothing was modelled: the 16:9 view saw black around them. The pillars'
    // own faces are seen nearly edge-on, so their skirts were long streaks. A
    // rock wall in each end plane is seen almost head-on instead, and the
    // near floor runs on to it. Each is the retail texture of a source
    // polygon (pillar rock, floor) in mirrored tiles at that polygon's texel
    // scale, every corner shaded like the scenery near it (lava glow by the
    // bank, dark purple elsewhere).
    // The walls stand about 2000 beyond the end pillars, whose lit rock
    // must still reach the wall foot near the camera.
    const int NativeWideKoalaLit = 48;
    const float NativeWideKoalaWallReach = 4500;
    static void NativeWideKoalaWalls(IMemory m, NativeWideWorld world, List<NativeWideRepair> output)
    {
        // Scenery colours, except the grey vertices of self-coloured textures (lava, rails).
        // The rock walls take only lit vertices (#156): the near floor and the
        // foot of the end pillars are painted black, and the wall low down by
        // them turned black too, read as holes between the pillar's rocks.
        var points = new List<(Vector3 Position, Vector3 Colour)>();
        var lit = new List<(Vector3 Position, Vector3 Colour)>();
        for (int vi = 0; vi < world.VertexCount; vi++)
        {
            var v = ReadNativeWideLocal(m, world, vi);
            if (Math.Abs(v.R - v.G) < 8 && Math.Abs(v.G - v.B) < 8 && v.R > 40) continue;
            points.Add((Position(v), new Vector3(v.R, v.G, v.B)));
            if (Math.Max(v.R, Math.Max(v.G, v.B)) >= NativeWideKoalaLit)
                lit.Add((Position(v), new Vector3(v.R, v.G, v.B)));
        }
        // Repair corners are mesh-local: the header holds the mesh translation.
        var local = new Vector3((int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 4),
            (int)m.ReadU32(world.Header + 8));
        NativeWideKoalaPlane(m, world, output, lit, 736, new Vector3(-4000, -640, -3840) - local,
            Vector3.UnitZ, 8960, Vector3.UnitY, 5440, NativeWideKoalaWallReach);
        NativeWideKoalaPlane(m, world, output, points, 141, new Vector3(-4000, 0, 1440) - local,
            Vector3.UnitX, 1800, Vector3.UnitZ, 3360);
        NativeWideKoalaPlane(m, world, output, lit, 767, new Vector3(4000, -640, -3840) - local,
            Vector3.UnitZ, 8960, Vector3.UnitY, 5440, NativeWideKoalaWallReach);
        NativeWideKoalaPlane(m, world, output, points, 84, new Vector3(2200, 0, 1440) - local,
            Vector3.UnitX, 1800, Vector3.UnitZ, 3360);
        NativeWideKoalaColumn(m, world, output, local, 734, 763, -1, 745);
        NativeWideKoalaColumn(m, world, output, local, 763, 775, 1, 769);
    }

    // The rock pillar at each arena end closes the 4:3 view's side, but it is
    // only a shell of front faces: in 16:9 the bands looked through its gaps
    // and past its outer side at the cave beyond and the end walls, so the
    // pillar seemed full of holes (#156). It becomes a solid rock column:
    // every pillar triangle swept 2000 outward along X (a prism each, its
    // foot taken below the floor), i.e. the pillar and all rock beyond it.
    // Only the prisms' far faces are drawn (wound outward, culled), so the
    // pillar's own rocks always stay in front: the column shows only through
    // the pillar's gaps and beyond its outer side, never on the arena side
    // (a convex hull filled the pillar's hollows there and cut the lava off
    // at the 4:3 edge).
    const float NativeWideKoalaColumnReach = 2000, NativeWideKoalaFoot = -300;
    // Every camera of the level's paths (intro fly-in included) lies in this
    // box; a prism face that points at all of it is always culled.
    static readonly Vector3 NativeWideKoalaCameraLow = new(-500, 200, 1400), NativeWideKoalaCameraHigh = new(700, 1350, 3950);
    static void NativeWideKoalaColumn(IMemory m, NativeWideWorld world, List<NativeWideRepair> output,
        Vector3 local, int first, int end, int outward, int texture)
    {
        if (world.PolyCount < end || !TryNativeWideKoalaTile(m, world, texture,
                out float uMin, out float uMax, out float vMin, out float vMax, out float cellU, out float cellV))
            return;
        var lit = new List<(Vector3 Position, Vector3 Colour)>();
        var prisms = new List<Vector3[]>();
        var shift = new Vector3(outward * NativeWideKoalaColumnReach, 0, 0);
        for (int pi = first; pi < end; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            var corners = new Vector3[6];
            int k = 0;
            foreach (int vi in new[] { a, b, c })
            {
                var v = ReadNativeWideLocal(m, world, vi);
                var p = Position(v);
                if (Math.Max(v.R, Math.Max(v.G, v.B)) >= NativeWideKoalaLit) lit.Add((p, new Vector3(v.R, v.G, v.B)));
                // Feet on the floor go below it, so no prism face lies in the floor.
                if (p.Y < 40) p.Y = NativeWideKoalaFoot;
                corners[k] = p;
                corners[k + 3] = p + shift;
                k++;
            }
            prisms.Add(corners);
        }
        foreach (var q in prisms)
        {
            var centre = (q[0] + q[1] + q[2] + q[3] + q[4] + q[5]) / 6;
            void Face(Vector3 a, Vector3 b, Vector3 c)
            {
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.Length() < 1) return;
                normal = Vector3.Normalize(normal);
                if (Vector3.Dot(normal, a - centre) < 0)
                {
                    (b, c) = (c, b);
                    normal = -normal;
                }
                bool seen = false;
                for (int corner = 0; corner < 8 && !seen; corner++)
                {
                    var camera = new Vector3(
                        (corner & 1) == 0 ? NativeWideKoalaCameraLow.X : NativeWideKoalaCameraHigh.X,
                        (corner & 2) == 0 ? NativeWideKoalaCameraLow.Y : NativeWideKoalaCameraHigh.Y,
                        (corner & 4) == 0 ? NativeWideKoalaCameraLow.Z : NativeWideKoalaCameraHigh.Z) - local;
                    seen = Vector3.Dot(normal, camera - a) < 0;
                }
                if (!seen) return;
                NativeWideKoalaTiledFace(output, texture, lit, a, b, c, normal, NativeWideKoalaEye - local,
                    uMin, uMax, vMin, vMax, cellU, cellV);
            }
            Face(q[0], q[1], q[2]);
            Face(q[3], q[4], q[5]);
            for (int e = 0; e < 3; e++)
            {
                int f = (e + 1) % 3;
                Face(q[e], q[f], q[f + 3]);
                Face(q[e], q[f + 3], q[e + 3]);
            }
        }
    }

    // One outward-wound triangle in mirrored texture tiles, wound outward and
    // culled: drawn only where the camera looks at the inside of the solid.
    // The tiles are laid as seen from the boss camera (a grid on the screen
    // of an eye at its usual spot, cast onto the face), so the column's far
    // faces read as one rock surface at an even scale: tiles laid in each
    // face's own plane turned into streaks on faces seen at a grazing angle.
    // Faces the eye sees edge-on keep tiles in their plane.
    const float NativeWideKoalaEyeDepth = 1800;
    static readonly Vector3 NativeWideKoalaEye = new(100, 1100, 3800);
    static void NativeWideKoalaTiledFace(List<NativeWideRepair> output, int texture,
        List<(Vector3 Position, Vector3 Colour)> lit, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Vector3 eye,
        float uMin, float uMax, float vMin, float vMax, float cellU, float cellV)
    {
        float planeD = Vector3.Dot(a, normal);
        bool cast = Math.Abs(Vector3.Dot(normal, Vector3.Normalize(eye - (a + b + c) / 3))) > 0.03f;
        float screenU = cellU / NativeWideKoalaEyeDepth, screenV = cellV / NativeWideKoalaEyeDepth;
        var axisU = Vector3.Normalize(Vector3.Cross(Math.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, normal));
        var axisV = Vector3.Cross(normal, axisU);
        Vector2 Flat(Vector3 p)
        {
            if (!cast) return new(Vector3.Dot(p, axisU) / cellU, Vector3.Dot(p, axisV) / cellV);
            float w = eye.Z - p.Z;
            return new((p.X - eye.X) / w / screenU, (p.Y - eye.Y) / w / screenV);
        }
        Vector3 Back(Vector2 f)
        {
            if (!cast) return axisU * (f.X * cellU) + axisV * (f.Y * cellV) + normal * planeD;
            var ray = new Vector3(f.X * screenU, f.Y * screenV, -1);
            return eye + ray * ((planeD - Vector3.Dot(normal, eye)) / Vector3.Dot(normal, ray));
        }
        Vector2 fa = Flat(a), fb = Flat(b), fc = Flat(c);
        // The clip wants a counter-clockwise triangle; the winding is set again in 3D.
        bool flip = (fb.X - fa.X) * (fc.Y - fa.Y) - (fb.Y - fa.Y) * (fc.X - fa.X) < 0;
        var corners = flip ? new[] { fa, fc, fb } : new[] { fa, fb, fc };
        int i0 = (int)MathF.Floor(Math.Min(fa.X, Math.Min(fb.X, fc.X)));
        int i1 = (int)MathF.Ceiling(Math.Max(fa.X, Math.Max(fb.X, fc.X)));
        int j0 = (int)MathF.Floor(Math.Min(fa.Y, Math.Min(fb.Y, fc.Y)));
        int j1 = (int)MathF.Ceiling(Math.Max(fa.Y, Math.Max(fb.Y, fc.Y)));
        for (int i = i0; i < i1; i++) for (int j = j0; j < j1; j++)
        {
            var cell = NativeWideClipToTriangle(
                [new(i, j), new(i + 1, j), new(i + 1, j + 1), new(i, j + 1)], corners);
            if (cell.Count < 3) continue;
            NativeWideClipVertex Corner(Vector2 f)
            {
                var p = Back(f);
                var colour = NativeWideKoalaShade(lit, p, NativeWideKoalaWallReach);
                float su = f.X - i, sv = f.Y - j;
                float u = (i & 1) == 0 ? uMin + su * (uMax - uMin) : uMax - su * (uMax - uMin);
                float v = (j & 1) == 0 ? vMax - sv * (vMax - vMin) : vMin + sv * (vMax - vMin);
                return new(p.X, p.Y, p.Z, colour.X, colour.Y, colour.Z, u, v);
            }
            var corner = cell.Select(Corner).ToArray();
            var p0 = Position(corner[0]);
            bool reverse = Vector3.Dot(Vector3.Cross(Position(corner[1]) - p0, Position(corner[2]) - p0), normal) < 0;
            for (int k = 1; k + 1 < corner.Length; k++)
                output.Add(reverse
                    ? new(texture, corner[0], corner[k + 1], corner[k], Boundary: true, Culled: true)
                    : new(texture, corner[0], corner[k], corner[k + 1], Boundary: true, Culled: true));
        }
    }

    // Sutherland-Hodgman: a convex polygon cut to a counter-clockwise triangle.
    static List<Vector2> NativeWideClipToTriangle(List<Vector2> polygon, Vector2[] triangle)
    {
        for (int e = 0; e < 3 && polygon.Count > 0; e++)
        {
            Vector2 a = triangle[e], b = triangle[(e + 1) % 3];
            float Side(Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            var kept = new List<Vector2>(polygon.Count + 1);
            for (int k = 0; k < polygon.Count; k++)
            {
                Vector2 p = polygon[k], q = polygon[(k + 1) % polygon.Count];
                float sp = Side(p), sq = Side(q);
                if (sp >= 0) kept.Add(p);
                if ((sp >= 0) != (sq >= 0)) kept.Add(p + (q - p) * (sp / (sp - sq)));
            }
            polygon = kept;
        }
        return polygon;
    }

    static bool TryNativeWideKoalaTile(IMemory m, NativeWideWorld world, int texture,
        out float uMin, out float uMax, out float vMin, out float vMax, out float cellU, out float cellV)
    {
        uMin = uMax = vMin = vMax = cellU = cellV = 0;
        if (!TryNativeWideMaterial(m, world, texture, 0, out _, out _,
            out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) return false;
        uMin = Math.Min(u0, Math.Min(u1, u2)); uMax = Math.Max(u0, Math.Max(u1, u2));
        vMin = Math.Min(v0, Math.Min(v1, v2)); vMax = Math.Max(v0, Math.Max(v1, v2));
        uint poly = world.Polygons + (uint)texture * 8;
        NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
        Vector3 pa = Position(ReadNativeWideLocal(m, world, a));
        float area = Vector3.Cross(Position(ReadNativeWideLocal(m, world, b)) - pa,
            Position(ReadNativeWideLocal(m, world, c)) - pa).Length();
        float uvArea = Math.Abs((u1 - u0) * (v2 - v0) - (v1 - v0) * (u2 - u0));
        if (area < 1 || uvArea < 1 || uMax - uMin < 1 || vMax - vMin < 1) return false;
        float density = MathF.Sqrt(uvArea / area);
        cellU = (uMax - uMin) / density;
        cellV = (vMax - vMin) / density;
        return true;
    }

    // Inverse-distance blend of the nearby scenery colours, fading to the
    // cave's dark purple away from them.
    static Vector3 NativeWideKoalaShade(List<(Vector3 Position, Vector3 Colour)> points, Vector3 p, float reach)
    {
        var dark = new Vector3(10, 7, 28);
        Vector3 sum = Vector3.Zero;
        float weights = 0, nearest = reach;
        foreach (var (position, colour) in points)
        {
            float d = Vector3.Distance(position, p);
            if (d >= reach) continue;
            nearest = Math.Min(nearest, d);
            float w = 1 / MathF.Max(d * d, 1);
            sum += colour * w;
            weights += w;
        }
        return weights == 0 ? dark : Vector3.Lerp(sum / weights, dark, nearest / reach);
    }

    static void NativeWideKoalaPlane(IMemory m, NativeWideWorld world, List<NativeWideRepair> output,
        List<(Vector3 Position, Vector3 Colour)> points, int texture, Vector3 origin,
        Vector3 alongU, float lengthU, Vector3 alongV, float lengthV, float reach = 2500)
    {
        if (!TryNativeWideKoalaTile(m, world, texture,
                out float uMin, out float uMax, out float vMin, out float vMax, out float cellU, out float cellV))
            return;
        int columns = (int)MathF.Ceiling(lengthU / cellU), rows = (int)MathF.Ceiling(lengthV / cellV);
        Vector3 At(int i, int j) => origin + alongU * (i * cellU) + alongV * (j * cellV);
        var shade = new Vector3[columns + 1, rows + 1];
        for (int i = 0; i <= columns; i++) for (int j = 0; j <= rows; j++)
            shade[i, j] = NativeWideKoalaShade(points, At(i, j), reach);
        for (int i = 0; i < columns; i++) for (int j = 0; j < rows; j++)
        {
            NativeWideClipVertex Corner(int di, int dj)
            {
                float u = ((i + di) & 1) == 0 ? uMin : uMax, v = ((j + dj) & 1) == 0 ? vMax : vMin;
                var p = At(i + di, j + dj);
                var colour = shade[i + di, j + dj];
                return new(p.X, p.Y, p.Z, colour.X, colour.Y, colour.Z, u, v);
            }
            output.Add(new(texture, Corner(0, 0), Corner(1, 0), Corner(1, 1)));
            output.Add(new(texture, Corner(0, 0), Corner(1, 1), Corner(0, 1)));
        }
    }

    // skirted: polygons whose open edges get a skirt (null: all of them).
    // river (Upstream): polygons with animated water vertices are never
    // continued: the river keeps its width, and a continued waterfall lip
    // reads as a flat blue panel beside the bank. Neither are compact pieces
    // (the tiki totems), whose cut sides would become long stretched stripes.
    // planar (Koala Kong): skirts keep the source face's texel scale (see
    // AddNativeWidePlanarStrip); the cave's small rock faces fan their skirts
    // far apart, and the plain strip turned them into streaks.
    // edgeReach (The Great Gate): single open edges, by polygon * 3 + edge,
    // each with its own skirt length; when set, only these are continued.
    const float NativeWideSkirtMinExtent = 1500;
    static void NativeWideSkirtRepairs(IMemory m, NativeWideWorld world, HashSet<int>? skirted,
        List<NativeWideRepair> output, bool river = false, bool planar = false,
        Dictionary<int, float>? edgeReach = null)
    {
        int count = world.PolyCount;
        var triangles = new NativeWideClipVertex[count * 3];
        var water = new bool[count];
        var edges = new Dictionary<NativeWideEdge, NativeWideEdgeOwner>();
        // Pieces: polygons joined by shared edges (union-find).
        var piece = new int[count];
        for (int pi = 0; pi < count; pi++) piece[pi] = pi;
        int Find(int p)
        {
            while (piece[p] != p) p = piece[p] = piece[piece[p]];
            return p;
        }
        for (int pi = 0; pi < count; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            int o = pi * 3;
            triangles[o] = ReadNativeWideLocal(m, world, a);
            triangles[o + 1] = ReadNativeWideLocal(m, world, b);
            triangles[o + 2] = ReadNativeWideLocal(m, world, c);
            if (river)
                water[pi] = ((FastU32(m, world.Vertices + (uint)a * 8 + 4) | FastU32(m, world.Vertices + (uint)b * 8 + 4)
                    | FastU32(m, world.Vertices + (uint)c * 8 + 4)) & 1) != 0;
            for (int ei = 0; ei < 3; ei++)
            {
                var edge = NativeWideEdgeKey(triangles[o + ei], triangles[o + (ei + 1) % 3]);
                if (edges.TryGetValue(edge, out var owner))
                {
                    edges[edge] = owner with { Count = owner.Count + 1 };
                    piece[Find(pi)] = Find(owner.Polygon);
                }
                else edges[edge] = new NativeWideEdgeOwner(pi, ei, 1);
            }
        }
        var pieceSize = new int[count];
        var low = new Vector3[count];
        var high = new Vector3[count];
        Array.Fill(low, new Vector3(float.MaxValue));
        Array.Fill(high, new Vector3(float.MinValue));
        for (int pi = 0; pi < count; pi++)
        {
            int root = Find(pi);
            pieceSize[root]++;
            for (int k = 0; k < 3; k++)
            {
                low[root] = Vector3.Min(low[root], Position(triangles[pi * 3 + k]));
                high[root] = Vector3.Max(high[root], Position(triangles[pi * 3 + k]));
            }
        }

        // Each open vertex moves along the mean of its open edges' outward
        // directions, so neighbouring strips share their outer corners.
        // Semi-transparent materials are the cut-out leaves and fronds (their
        // textures are the only ones with transparent texels): never continued.
        var open = new List<(NativeWideEdgeOwner Owner, Vector2[] Uv)>();
        var corner = new Dictionary<Vector3, Vector3>();
        foreach (var owner in edges.Values)
        {
            int piece0 = Find(owner.Polygon);
            if (owner.Count != 1 || water[owner.Polygon] || pieceSize[piece0] < NativeWideSkirtMinPiece) continue;
            var extent = high[piece0] - low[piece0];
            if (river && MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) < NativeWideSkirtMinExtent) continue;
            int o = owner.Polygon * 3;
            var a = triangles[o + owner.Edge];
            var b = triangles[o + (owner.Edge + 1) % 3];
            var c = triangles[o + (owner.Edge + 2) % 3];
            var outward = NativeWideOutward(a, b, c);
            if (outward.LengthSquared() < 1) continue;
            if (!TryNativeWideMaterial(m, world, owner.Polygon, 0, out var flags, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)
                || !flags.Textured || flags.SemiTrans) continue;
            outward = Vector3.Normalize(outward);
            if (edgeReach?.ContainsKey(owner.Polygon * 3 + owner.Edge) ?? skirted?.Contains(owner.Polygon) ?? true)
                open.Add((owner, [new(u0, v0), new(u1, v1), new(u2, v2)]));
            corner[Position(a)] = corner.GetValueOrDefault(Position(a)) + outward;
            corner[Position(b)] = corner.GetValueOrDefault(Position(b)) + outward;
        }
        foreach (var (owner, uv) in open)
        {
            int o = owner.Polygon * 3;
            var a = triangles[o + owner.Edge];
            var b = triangles[o + (owner.Edge + 1) % 3];
            var c = triangles[o + (owner.Edge + 2) % 3];
            var own = Vector3.Normalize(NativeWideOutward(a, b, c));
            if (planar)
            {
                AddNativeWidePlanarStrip(output, owner.Polygon, a, b, c,
                    uv[owner.Edge], uv[(owner.Edge + 1) % 3], uv[(owner.Edge + 2) % 3],
                    NativeWideSkirtDirection(corner[Position(a)], own), NativeWideSkirtDirection(corner[Position(b)], own),
                    NativeWideSkirtDistance);
                continue;
            }
            AddNativeWideSceneryStrip(output, owner.Polygon, a, b, c,
                uv[owner.Edge], uv[(owner.Edge + 1) % 3], uv[(owner.Edge + 2) % 3],
                NativeWideSkirtDirection(corner[Position(a)], own), NativeWideSkirtDirection(corner[Position(b)], own),
                edgeReach?.GetValueOrDefault(owner.Polygon * 3 + owner.Edge) ?? NativeWideSkirtDistance);
        }
    }

    // A corner's mean direction; where its edges point apart (a thin spike),
    // the edge's own direction.
    static Vector3 NativeWideSkirtDirection(Vector3 sum, Vector3 own) =>
        sum.LengthSquared() < 0.25f ? own : Vector3.Normalize(sum);
}
