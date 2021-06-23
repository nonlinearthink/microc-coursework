package type;

public class ArrayType extends BaseType{
    private BaseType[] value;

    public ArrayType() {
    }

    public ArrayType(BaseType[] value) {
        this.value = value;
    }

    public BaseType[] getValue() {
        return value;
    }

    public void setValue(BaseType[] value) {
        this.value = value;
    }

}
