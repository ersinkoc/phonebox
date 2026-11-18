// Places in Israel for the weather location picker on the settings page (Settings › General › Weather).
// Each entry: [Hebrew name, English name, latitude, longitude, region]. Coordinates are rounded to three decimals,
// which is what the weather settings store; a forecast grid cell is far larger than that.
(() => {
  'use strict';

  const REGIONS = Object.freeze([
    { id: 'north', he: 'צפון', en: 'North' },
    { id: 'haifa', he: 'חיפה והסביבה', en: 'Haifa area' },
    { id: 'sharon', he: 'השרון', en: 'Sharon' },
    { id: 'telaviv', he: 'תל אביב וגוש דן', en: 'Tel Aviv area' },
    { id: 'center', he: 'מרכז והשפלה', en: 'Centre and Shfela' },
    { id: 'jerusalem', he: 'ירושלים והסביבה', en: 'Jerusalem area' },
    { id: 'south', he: 'דרום', en: 'South' },
  ]);

  const PLACES = Object.freeze([
    ['קריית שמונה', 'Kiryat Shmona', 33.207, 35.570, 'north'],
    ['קצרין', 'Katzrin', 32.993, 35.691, 'north'],
    ['צפת', 'Safed', 32.966, 35.496, 'north'],
    ['מעלות-תרשיחא', 'Ma\'alot-Tarshiha', 33.017, 35.271, 'north'],
    ['נהריה', 'Nahariya', 33.006, 35.095, 'north'],
    ['עכו', 'Acre', 32.928, 35.076, 'north'],
    ['כרמיאל', 'Karmiel', 32.914, 35.292, 'north'],
    ['טבריה', 'Tiberias', 32.794, 35.531, 'north'],
    ['נצרת', 'Nazareth', 32.700, 35.297, 'north'],
    ['נוף הגליל', 'Nof HaGalil', 32.706, 35.324, 'north'],
    ['מגדל העמק', 'Migdal HaEmek', 32.676, 35.240, 'north'],
    ['יקנעם עילית', 'Yokneam Illit', 32.659, 35.110, 'north'],
    ['עפולה', 'Afula', 32.607, 35.290, 'north'],
    ['בית שאן', 'Beit She\'an', 32.497, 35.497, 'north'],

    ['חיפה', 'Haifa', 32.794, 34.989, 'haifa'],
    ['קריית ים', 'Kiryat Yam', 32.850, 35.068, 'haifa'],
    ['קריית ביאליק', 'Kiryat Bialik', 32.838, 35.086, 'haifa'],
    ['קריית מוצקין', 'Kiryat Motzkin', 32.837, 35.078, 'haifa'],
    ['קריית אתא', 'Kiryat Ata', 32.811, 35.113, 'haifa'],
    ['נשר', 'Nesher', 32.770, 35.040, 'haifa'],
    ['טירת כרמל', 'Tirat Carmel', 32.760, 34.972, 'haifa'],
    ['זכרון יעקב', 'Zikhron Ya\'akov', 32.571, 34.953, 'haifa'],
    ['אום אל-פחם', 'Umm al-Fahm', 32.517, 35.153, 'haifa'],
    ['קיסריה', 'Caesarea', 32.519, 34.905, 'haifa'],
    ['פרדס חנה-כרכור', 'Pardes Hanna-Karkur', 32.474, 34.975, 'haifa'],
    ['חדרה', 'Hadera', 32.434, 34.920, 'haifa'],

    ['נתניה', 'Netanya', 32.332, 34.860, 'sharon'],
    ['טייבה', 'Tayibe', 32.266, 35.009, 'sharon'],
    ['רעננה', 'Ra\'anana', 32.184, 34.871, 'sharon'],
    ['כפר סבא', 'Kfar Saba', 32.175, 34.907, 'sharon'],
    ['הרצליה', 'Herzliya', 32.166, 34.844, 'sharon'],
    ['הוד השרון', 'Hod HaSharon', 32.150, 34.888, 'sharon'],
    ['רמת השרון', 'Ramat HaSharon', 32.146, 34.840, 'sharon'],
    ['ראש העין', 'Rosh HaAyin', 32.096, 34.957, 'sharon'],

    ['תל אביב-יפו', 'Tel Aviv-Yafo', 32.085, 34.782, 'telaviv'],
    ['פתח תקווה', 'Petah Tikva', 32.087, 34.888, 'telaviv'],
    ['בני ברק', 'Bnei Brak', 32.084, 34.834, 'telaviv'],
    ['גבעתיים', 'Givatayim', 32.072, 34.812, 'telaviv'],
    ['רמת גן', 'Ramat Gan', 32.068, 34.824, 'telaviv'],
    ['קריית אונו', 'Kiryat Ono', 32.064, 34.855, 'telaviv'],
    ['אור יהודה', 'Or Yehuda', 32.031, 34.851, 'telaviv'],
    ['בת ים', 'Bat Yam', 32.017, 34.745, 'telaviv'],
    ['חולון', 'Holon', 32.011, 34.775, 'telaviv'],

    ['אלעד', 'El\'ad', 32.052, 34.951, 'center'],
    ['שוהם', 'Shoham', 31.999, 34.946, 'center'],
    ['ראשון לציון', 'Rishon LeZion', 31.973, 34.790, 'center'],
    ['לוד', 'Lod', 31.951, 34.888, 'center'],
    ['נס ציונה', 'Ness Ziona', 31.930, 34.799, 'center'],
    ['רמלה', 'Ramla', 31.929, 34.871, 'center'],
    ['מודיעין-מכבים-רעות', 'Modi\'in-Maccabim-Re\'ut', 31.898, 35.010, 'center'],
    ['רחובות', 'Rehovot', 31.894, 34.811, 'center'],
    ['יבנה', 'Yavne', 31.878, 34.739, 'center'],
    ['גדרה', 'Gedera', 31.814, 34.777, 'center'],
    ['קריית מלאכי', 'Kiryat Malakhi', 31.730, 34.746, 'center'],
    ['קריית גת', 'Kiryat Gat', 31.610, 34.764, 'center'],

    ['ירושלים', 'Jerusalem', 31.769, 35.216, 'jerusalem'],
    ['מבשרת ציון', 'Mevaseret Zion', 31.802, 35.150, 'jerusalem'],
    ['מעלה אדומים', 'Ma\'ale Adumim', 31.777, 35.298, 'jerusalem'],
    ['בית שמש', 'Beit Shemesh', 31.747, 34.988, 'jerusalem'],
    ['מודיעין עילית', 'Modi\'in Illit', 31.933, 35.043, 'jerusalem'],
    ['ביתר עילית', 'Beitar Illit', 31.697, 35.115, 'jerusalem'],
    ['אפרת', 'Efrat', 31.654, 35.154, 'jerusalem'],
    ['אריאל', 'Ariel', 32.105, 35.171, 'jerusalem'],

    ['אשדוד', 'Ashdod', 31.804, 34.655, 'south'],
    ['אשקלון', 'Ashkelon', 31.669, 34.571, 'south'],
    ['שדרות', 'Sderot', 31.525, 34.596, 'south'],
    ['נתיבות', 'Netivot', 31.421, 34.588, 'south'],
    ['רהט', 'Rahat', 31.393, 34.754, 'south'],
    ['אופקים', 'Ofakim', 31.314, 34.620, 'south'],
    ['ערד', 'Arad', 31.259, 35.213, 'south'],
    ['באר שבע', 'Be\'er Sheva', 31.252, 34.791, 'south'],
    ['ים המלח (עין בוקק)', 'Dead Sea (Ein Bokek)', 31.200, 35.363, 'south'],
    ['דימונה', 'Dimona', 31.069, 35.033, 'south'],
    ['ירוחם', 'Yeruham', 30.988, 34.930, 'south'],
    ['מצפה רמון', 'Mitzpe Ramon', 30.610, 34.801, 'south'],
    ['אילת', 'Eilat', 29.558, 34.948, 'south'],
  ].map(([he, en, lat, lon, region]) => Object.freeze({ he, en, lat, lon, region })));

  /** Hebrew names when the interface is Hebrew, or when it follows a Hebrew system the dictionary does not cover yet. */
  function hebrew() {
    const I18n = window.PhoneBoxI18n;
    if (I18n && I18n.getLang() === 'he') return true;
    const auto = !I18n || I18n.getPref() === 'auto';
    return auto && /^(he|iw)\b/i.test(navigator.language || '');
  }

  window.PhoneBoxIL = Object.freeze({ REGIONS, PLACES, hebrew });
})();
