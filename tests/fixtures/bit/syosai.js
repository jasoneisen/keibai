$(document).ready(function() {
    $("#threeSetPDF").click(function(event) {
    
        var courtId = $("#courtId").val();
        var saleUnitId = $("#saleUnitId").val();
        event.preventDefault();
        event.stopPropagation();

        $.ajax({
            url: '/app/detail/pd001/h03',
            type: 'POST',
            data: {
                "courtId" : courtId,
                "saleUnitId" : saleUnitId
            },
            dataType: 'text'
        })
        .done(function(data, textStatus, jqXHR) {
            if (data.match(/success/)) {
                // SUCCESS
                url = location.protocol + "//" + location.host + "/app/detail/pd001/h04?courtId=" + courtId + "&saleUnitId=" + saleUnitId;
                location.href = url;
            } else {
                // FAILURE
                commonSubmit("downloadError");
            }
        })
        .fail(function(jqXHR, textStatus, errorThrown) {
            commonSubmit("downloadError");
        })
        .always(function(jqXHR, textStatus) {
        });
        return false;
    });
    
    tabShow();
    
    $('#chiiki-tab').on('shown.bs.tab', function (e) {
  		// 地図取得のAPIの実行
  		var latitude = $("#latitude").val();
        var longitude = $("#longitude").val();
	    var scl = 18;
	 
	    var map = L.map.mapion('ZMap', {
	        center: L.latLng(latitude, longitude),
	        zoom: scl
	    });
	    map.centerMarkHide();
	})
    
 });
 
     
   function tabShow( ){
        transitionTabId= $("#transitionTabId").val();
       if("1"==transitionTabId){
           $("#bloc-tab").trigger("click");
       }else if("2"==transitionTabId){
       
           $("#chiiki-tab").trigger("click"); 
       }
    }
    
	 function commonSubmit(eventID){
	 
	   if (eventID == 'downloadError') {
	      	window.location.href = "/error";
	   } else {
	   	    uri = "/app/" + contextPathTbl[eventID];
	        form = document.getElementById("dataForm");
	        form.action = uri;
	        form.method="post";
	        form.submit();
	   }
	}
	
	
	 function setSetailsTabFromKbn(value){
	   $("#detailsTabFromKbn").val(value);
	 }
 
    contextPathTbl = {
	    "SEARCH001":"detail/pd001/h06",
	    "SEARCHRESULTS001":"detail/pd001/h07",
	    "SCHEDULE001":"detail/pd001/h08",
	    "AREA001":"areaSelect/pk001/h01"
       };
    

