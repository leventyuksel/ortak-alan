# Ortak Alan

Ortak Alan, Windows bilgisayarların birbirine klasör, yazıcı, mesaj ve dosya ulaştırması için yapılır. Aynı ağdaki ya da Tailscale ile birbirini gören bilgisayarlar listede belirir. Her bilgisayar kendi adıyla görünür.

Kurulum için [son sürümdeki](https://github.com/leventyuksel/ortak-alan/releases/latest) OrtakAlan-Setup.exe dosyasını çalıştırın. Ayrıntılı anlatım Ortak Alan.pdf dosyasındadır.

## Amaç

Program, yan yana ya da uzakta duran bilgisayarları tek pencereden birbirine bağlar. Bir klasörü açmak, bir yazıcıyı kullanmak ya da kısa bir mesaj ile dosya göndermek için ayrı araçlar kurmaya gerek kalmaz. İlk bağlantıda karşı bilgisayar bir kez izin verir. İzin verilen bilgisayar bir daha sormaz.

## Ne yapabilir

- **Sohbet.** Seçilen kişiye mesaj, dosya ve ekran görüntüsü gider. Karşı taraf kapalıysa yazılan, açılınca iletilir.
- **Ortak konuşma.** Ağdaki herkes aynı yere yazar ve gönderilen dosyayı görür. Dosyaya tıklanınca alınır.
- **Klasör.** Seçilen klasör paylaşılır. Karşı taraf içinde gezinebilir, kopyalayabilir ve dosya bırakabilir.
- **Yazıcı.** Bu bilgisayarın yazıcısı paylaşılır. Karşı taraf onu kendi yazıcılarına ekler ve oradan çıktı alır.
- **Mesaj araçları.** Bağlantı tıklanınca açılır. Metin seçilip kopyalanır. Yıldızlı mesajlar süzülür. Sık yazılan cümle tek tıkla gönderilir.
- **Görünüm.** Beş aydınlık ve beş karanlık hazır temadan biri seçilir. Yazı rengi temanın içindedir. Program Windows ile birlikte açılabilir.

## Nasıl kullanılır

### İlk açılış

1. Programı açın. Ağda görünecek adı yazıp Devam deyin. Diğer bilgisayarlar sizi bu adla görür.
2. Masaüstüne Ortak Alan kısayolu gelir. Paylaşılan Dosyalar klasörü de masaüstünde durur.
3. Program, görev çubuğunun sağındaki simgede kalır. Simgeye tıklamak sohbeti açar. Sağ tık menüsünde Sohbet, Paylaşımlar, Paylaşılan Dosyalar, Ayarlar ve Çıkış vardır.
4. Pencereyi kapatmak programı durdurmaz. Tamamen kapatmak için simgeden Çıkış seçilir.

### Kişiler

Solda Ortak konuşma ve kişi listesi durur. Çevrimiçi kişi renkli görünür. Tailscale üzerinden gelenin yanında bunu belirten bir yazı vardır. Çevrimdışı kişiye yazılan mesaj, o bilgisayar programı açınca gider.

Bir kişiye ilk kez yazarken karşı ekranda İzin ver ve Reddet sorulur. Bu soru o bilgisayar için bir kez çıkar. Kişiye sağ tıklayınca paylaşılan klasörüne ya da yazıcısına bağlanılır. Listeden kaldır, kişiyi bu bilgisayarın listesinden gizler.

### Mesaj göndermek

Soldan Ortak konuşmayı ya da bir kişiyi seçin. Alta yazıp Gönder’e basın. Enter da gönderir. Dosya düğmesi dosya seçtirir. Dosyayı ya da klasörü pencereye sürükleyip bırakmak da gönderir. Klasörün içindeki dosyalar teker teker gider. Bir dosya en çok 150 MB olabilir.

Ekran düğmesi ekrandan bir dikdörtgen seçtirir. Kalem, fosforlu kalem ve silgi ile üzerine çizilir. Sohbete gönder ya da yalnızca kaydet seçilir.

Gelen dosya bir kart olarak durur. Karta tıklanınca dosya alınır ve açılır. Alınan dosya masaüstündeki Paylaşılan Dosyalar klasörüne konur.

### Bağlantı ve kopyalama

http, https ya da www ile başlayan adresin altı çizilir. Üzerine gelince imleç el olur. Tıklamak adresi tarayıcıda açar. Metin fareyle sürüklenerek seçilir. Ctrl+C seçilen parçayı, seçim yoksa mesajın tamamını kopyalar. Sağ tık menüsünde Seçileni kopyala ve Mesajı kopyala da vardır.

### Favoriler

Mesajın yanındaki yıldız, o mesajı favoriye alır. Üstteki Favoriler düğmesi yalnızca bu konuşmada yıldızlanan mesajları gösterir. Aynı düğmeye tekrar basınca konuşmanın tamamı geri gelir. Yıldız her konuşmada ayrı durur.

### Sabit mesajlar

Sık kullanılan cümleler bir kez kaydedilir. Sabit mesajlar düğmesi listeyi açar. Bir satıra tıklamak o cümleyi açık olan konuşmaya gönderir. Yeni cümle kutuya yazılıp Kaydet ile eklenir. Gönderilmiş bir mesaj, sağ tık menüsündeki Sabit mesajlara ekle ile de listeye alınır. Çıkar, cümleyi yalnızca listeden siler. Liste bütün konuşmalarda ortaktır.

### Mesaj silmek

Konuşmanın tamamını silen bir komut yoktur. Yalnızca tek mesaj silinir. Mesajın üzerine sağ tıklayıp Sil derseniz o mesaj sizin ekranınızdan kalkar. Karşı tarafta metin durur ve silindiği anlaşılsın diye üzeri çizili görünür. Henüz karşıya gitmemiş bir mesaj silinirse oraya hiç ulaşmaz. Çizginin görünmesi için iki bilgisayarda da güncel programın açık olması gerekir.

### Klasör paylaşmak

Üstteki Klasör paylaş ile bir klasör seçilir. Klasör, Paylaşımlar penceresinde listelenir. Karşı taraf kişiye sağ tıklayıp paylaşılan klasöre bağlanır ya da Paylaşılan Dosyalar penceresinden o bilgisayarı açar. Bu pencerede dosya açılır, kopyalanır, yapıştırılır, adı değiştirilir ve silinir. Yeni klasör de oluşturulabilir. Masaüstündeki Paylaşılan Dosyalar klasörü aynı yere açılır.

### Yazıcı paylaşmak

Yazıcı paylaş, bu bilgisayardaki yazıcılardan birini seçtirir. Karşı taraf kişiye sağ tıklayıp paylaşılan yazıcıya bağlanır. Yazıcı, Windows’un yazıcı listesine eklenir. Oradan alınan çıktı, paylaşan bilgisayarın yazıcısına gider.

### Aramak

Kişiler kutusuna yazılan ad, listeyi süzer. Konuşmada ara kutusu açık sohbette mesaj metnine, kişi adına ve dosya adına bakar.

### Ayarlar ve güncelleme

Ayarlardan bilgisayar adı, Windows ile açılma ve hazır temalardan biri değişir. Beş aydınlık ve beş karanlık tema vardır; yazı rengi temayla birlikte gelir. Sağ üstteki işaret künyeyi açar. Künyede sürüm görünür ve güncelleme kurulur. Yeni sürüm varken penceredeki sarı ünlem de kurulumu başlatır. Program açıldığında daha yeni bir sürüm varsa paket indirilir ve kurulum yenilenir.

### Lisans

Program 30 günlük deneme ile açılır. Süre dolunca lisans önerilir; program çalışmayı sürdürür. Lisanssız en çok beş kullanıcı eklenir. Altıncı kullanıcı için lisans gerekir. Anahtar künyedeki kutuya yapıştırılıp kaydedilir. Anahtar bir bilgisayarda girilince aynı ağdaki diğer bilgisayarlar programı yeniden açtığında lisanslı olur. Program klasörü başka bilgisayara kopyalanırsa orada açılmaz.

## Birlikte çalışması için

İki bilgisayarda da program açık olmalıdır. Bilgisayarlar aynı yerel ağda olmalı ya da Tailscale ile birbirini görmelidir. İlk bağlantı, karşı tarafın iznine bağlıdır.

İletişim: LvntYxl@gmail.com. Bu yazılım klasör, yazıcı ve mesaj paylaşımı için üretilmiştir. Kopyalanması ve satılması yasaktır.
